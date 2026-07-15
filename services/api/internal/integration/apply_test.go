package integration

import (
	"context"
	"encoding/json"
	"testing"

	"living-world-survival/services/api/internal/itemdef"
	"living-world-survival/services/api/internal/store"
	survivalv1 "living-world-survival/services/gen/go/survival/v1"
)

// These tests exercise the M3 AppendEvents effect application (06B 3.2): each
// Domain Event durably mutates inventory_entries / item_instances /
// currency_ledger / world_items / world_blueprints in the same transaction that
// persists the event. They run against the real PostgreSQL (self-skipped by
// setup when none is reachable).

// seedItems seeds the Item Definition master into the test DB so stack limits /
// is_instance are available to the applier (apid seeds this at runtime; the
// integration harness seeds it explicitly).
func (h *harness) seedItems(t *testing.T) {
	t.Helper()
	cat, err := itemdef.Load("../../data/item_definitions.json")
	if err != nil {
		t.Fatalf("load item master: %v", err)
	}
	if err := cat.Seed(context.Background(), h.store); err != nil {
		t.Fatalf("seed item master: %v", err)
	}
}

// mkEvent builds a DomainEvent with a JSON payload marshalled from v.
func mkEvent(t *testing.T, worldID, typ, aggregate string, local int64, v any) *survivalv1.DomainEvent {
	t.Helper()
	payload, err := json.Marshal(v)
	if err != nil {
		t.Fatalf("marshal payload: %v", err)
	}
	return &survivalv1.DomainEvent{
		EventId:          "ev-" + store.NewUUID(),
		WorldId:          worldID,
		AggregateId:      aggregate,
		LocalSequence:    local,
		Type:             typ,
		Payload:          payload,
		OccurredAtUnixMs: 1_700_000_000_000 + local,
	}
}

func (h *harness) append(t *testing.T, evs ...*survivalv1.DomainEvent) *survivalv1.AppendEventsResponse {
	t.Helper()
	resp, err := h.world.AppendEvents(context.Background(),
		&survivalv1.AppendEventsRequest{ServerId: "apply-test", Events: evs})
	if err != nil {
		t.Fatalf("AppendEvents: %v", err)
	}
	return resp
}

// invQty returns the total stackable quantity of an item in an owner's inventory.
func (h *harness) invQty(t *testing.T, owner, itemDef string) int {
	t.Helper()
	var n int
	err := h.pool.QueryRow(context.Background(),
		`SELECT coalesce(sum(e.quantity), 0)
		   FROM inventory_entries e JOIN inventories i ON e.inventory_id = i.inventory_id
		  WHERE i.owner_type = 'character' AND i.owner_id = $1
		    AND e.item_definition_id = $2 AND e.item_instance_id IS NULL`,
		owner, itemDef).Scan(&n)
	if err != nil {
		t.Fatalf("invQty: %v", err)
	}
	return n
}

func (h *harness) invVersion(t *testing.T, owner string) int64 {
	t.Helper()
	var v int64
	err := h.pool.QueryRow(context.Background(),
		`SELECT version FROM inventories WHERE owner_type = 'character' AND owner_id = $1`, owner).Scan(&v)
	if err != nil {
		t.Fatalf("invVersion: %v", err)
	}
	return v
}

func okResults(t *testing.T, resp *survivalv1.AppendEventsResponse) {
	t.Helper()
	for i, r := range resp.GetResults() {
		if r != survivalv1.ResultStatus_RESULT_STATUS_OK {
			t.Fatalf("event %d: got %v want OK", i, r)
		}
	}
}

// TestMinedGrantsInventory: resource.mined grants are added and version bumps.
func TestMinedGrantsInventory(t *testing.T) {
	h := setup(t)
	h.seedItems(t)
	worldID := h.newWorld(t)
	owner := store.NewUUID()

	ev := mkEvent(t, worldID, "resource.mined", "node-1", 1, map[string]any{
		"node_id":       "node-1",
		"resource_type": "stone",
		"actor_id":      owner, // recipient (06B contract addendum)
		"grants":        []map[string]any{{"item_definition_id": "stone", "quantity": 7}},
	})
	okResults(t, h.append(t, ev))

	if got := h.invQty(t, owner, "stone"); got != 7 {
		t.Fatalf("stone qty: got %d want 7", got)
	}
	if v := h.invVersion(t, owner); v != 1 {
		t.Fatalf("inventory version: got %d want 1", v)
	}

	// Idempotency (AT-003 / AT-021 思想): resending the same event_id is a no-op —
	// the grant is not applied twice.
	resp := h.append(t, ev)
	if resp.GetResults()[0] != survivalv1.ResultStatus_RESULT_STATUS_DUPLICATE {
		t.Fatalf("resend: got %v want DUPLICATE", resp.GetResults()[0])
	}
	if got := h.invQty(t, owner, "stone"); got != 7 {
		t.Fatalf("stone qty after resend: got %d want 7 (no double-apply)", got)
	}
}

// TestStackSplitsAcrossSlots: a grant exceeding stack_limit spans multiple slots.
func TestStackSplitsAcrossSlots(t *testing.T) {
	h := setup(t)
	h.seedItems(t)
	worldID := h.newWorld(t)
	owner := store.NewUUID()

	// stone stack_limit = 50; grant 120 → 50 + 50 + 20 across 3 slots.
	ev := mkEvent(t, worldID, "resource.mined", "node-2", 1, map[string]any{
		"actor_id": owner,
		"grants":   []map[string]any{{"item_definition_id": "stone", "quantity": 120}},
	})
	okResults(t, h.append(t, ev))
	if got := h.invQty(t, owner, "stone"); got != 120 {
		t.Fatalf("stone qty: got %d want 120", got)
	}
	var slots int
	if err := h.pool.QueryRow(context.Background(),
		`SELECT count(*) FROM inventory_entries e JOIN inventories i ON e.inventory_id=i.inventory_id
		  WHERE i.owner_id=$1 AND e.item_definition_id='stone'`, owner).Scan(&slots); err != nil {
		t.Fatalf("count slots: %v", err)
	}
	if slots != 3 {
		t.Fatalf("expected 3 slots for 120/50, got %d", slots)
	}
}

// TestRecipeConsumeProduce: station.job_completed consumes ingredients and
// produces the output (AT-005 永続面); an instance output creates item_instances.
func TestRecipeConsumeProduce(t *testing.T) {
	h := setup(t)
	h.seedItems(t)
	worldID := h.newWorld(t)
	owner := store.NewUUID()

	// Pre-stock the ingredients via a mined grant.
	stock := mkEvent(t, worldID, "resource.mined", "node-3", 1, map[string]any{
		"actor_id": owner,
		"grants": []map[string]any{
			{"item_definition_id": "stone", "quantity": 5},
			{"item_definition_id": "wood", "quantity": 2},
		},
	})
	okResults(t, h.append(t, stock))

	instanceID := "inst-" + store.NewUUID()
	craft := mkEvent(t, worldID, "station.job_completed", "anvil-1", 2, map[string]any{
		"station_id": "anvil-1",
		"recipe_id":  "stone_pickaxe",
		"actor_id":   owner,
		"consumed": []map[string]any{
			{"item_definition_id": "stone", "quantity": 5},
			{"item_definition_id": "wood", "quantity": 2},
		},
		"produced": []map[string]any{
			{"item_definition_id": "stone_pickaxe", "quantity": 1, "item_instance_id": instanceID},
		},
	})
	okResults(t, h.append(t, craft))

	if got := h.invQty(t, owner, "stone"); got != 0 {
		t.Fatalf("stone after craft: got %d want 0", got)
	}
	if got := h.invQty(t, owner, "wood"); got != 0 {
		t.Fatalf("wood after craft: got %d want 0", got)
	}
	// The produced pickaxe is an individual: item_instances row + a slot referencing it.
	var defID string
	if err := h.pool.QueryRow(context.Background(),
		`SELECT definition_id FROM item_instances WHERE item_instance_id = $1`, instanceID).Scan(&defID); err != nil {
		t.Fatalf("item_instances lookup: %v", err)
	}
	if defID != "stone_pickaxe" {
		t.Fatalf("instance def: got %q want stone_pickaxe", defID)
	}
	var instSlots int
	if err := h.pool.QueryRow(context.Background(),
		`SELECT count(*) FROM inventory_entries e JOIN inventories i ON e.inventory_id=i.inventory_id
		  WHERE i.owner_id=$1 AND e.item_instance_id=$2`, owner, instanceID).Scan(&instSlots); err != nil {
		t.Fatalf("count instance slot: %v", err)
	}
	if instSlots != 1 {
		t.Fatalf("expected 1 slot for the pickaxe instance, got %d", instSlots)
	}
}

// TestCookingProducesWaste: cooking.completed consumes raw_meat and produces
// cooked_meat + food_waste (AT-009 永続面).
func TestCookingProducesWaste(t *testing.T) {
	h := setup(t)
	h.seedItems(t)
	worldID := h.newWorld(t)
	owner := store.NewUUID()

	stock := mkEvent(t, worldID, "resource.mined", "n", 1, map[string]any{
		"actor_id": owner,
		"grants":   []map[string]any{{"item_definition_id": "raw_meat", "quantity": 2}},
	})
	okResults(t, h.append(t, stock))

	cook := mkEvent(t, worldID, "cooking.completed", "cook-1", 2, map[string]any{
		"station_id": "cook-1",
		"actor_id":   owner,
		"consumed":   []map[string]any{{"item_definition_id": "raw_meat", "quantity": 1}},
		"produced": []map[string]any{
			{"item_definition_id": "cooked_meat", "quantity": 1},
			{"item_definition_id": "food_waste", "quantity": 1},
		},
	})
	okResults(t, h.append(t, cook))

	if got := h.invQty(t, owner, "raw_meat"); got != 1 {
		t.Fatalf("raw_meat: got %d want 1", got)
	}
	if got := h.invQty(t, owner, "cooked_meat"); got != 1 {
		t.Fatalf("cooked_meat: got %d want 1", got)
	}
	if got := h.invQty(t, owner, "food_waste"); got != 1 {
		t.Fatalf("food_waste: got %d want 1", got)
	}
}

// TestDiscardThenClean: item.discarded persists a world_item and removes it from
// inventory; cleaning.completed deletes it and credits a currency reward (AT-010).
func TestDiscardThenClean(t *testing.T) {
	h := setup(t)
	h.seedItems(t)
	worldID := h.newWorld(t)
	owner := store.NewUUID()

	stock := mkEvent(t, worldID, "resource.mined", "n", 1, map[string]any{
		"actor_id": owner,
		"grants":   []map[string]any{{"item_definition_id": "food_waste", "quantity": 3}},
	})
	okResults(t, h.append(t, stock))

	worldItemID := "wi-" + store.NewUUID()
	discard := mkEvent(t, worldID, "item.discarded", owner, 2, map[string]any{
		"actor_id":           owner,
		"world_item_id":      worldItemID,
		"item_definition_id": "food_waste",
		"quantity":           1,
		"position":           map[string]float64{"x": 1, "y": 2, "z": 3}, //nolint:forbidigo // 座標は float 許容（13.1）
		"tags":               []string{"waste.food"},
	})
	okResults(t, h.append(t, discard))

	if got := h.invQty(t, owner, "food_waste"); got != 2 {
		t.Fatalf("food_waste after discard: got %d want 2", got)
	}
	var wiCount int
	if err := h.pool.QueryRow(context.Background(),
		`SELECT count(*) FROM world_items WHERE world_item_id=$1 AND world_id=$2`, worldItemID, worldID).Scan(&wiCount); err != nil {
		t.Fatalf("world_items lookup: %v", err)
	}
	if wiCount != 1 {
		t.Fatalf("world_items: got %d want 1", wiCount)
	}

	clean := mkEvent(t, worldID, "cleaning.completed", worldItemID, 3, map[string]any{
		"world_item_id":               worldItemID,
		"disposed_item_definition_id": "food_waste",
		"owner_id":                    owner,
		"reward_amount":               5,
	})
	okResults(t, h.append(t, clean))

	if err := h.pool.QueryRow(context.Background(),
		`SELECT count(*) FROM world_items WHERE world_item_id=$1`, worldItemID).Scan(&wiCount); err != nil {
		t.Fatalf("world_items recount: %v", err)
	}
	if wiCount != 0 {
		t.Fatalf("world_item should be disposed, still %d", wiCount)
	}
	var balance int64
	if err := h.pool.QueryRow(context.Background(),
		`SELECT balance_after FROM currency_ledger WHERE owner_id=$1 ORDER BY created_at DESC LIMIT 1`, owner).Scan(&balance); err != nil {
		t.Fatalf("currency_ledger lookup: %v", err)
	}
	if balance != 5 {
		t.Fatalf("cleaning reward balance: got %d want 5", balance)
	}
}

// TestBlueprintUnlock: development.blueprint_unlocked upserts world_blueprints.
func TestBlueprintUnlock(t *testing.T) {
	h := setup(t)
	worldID := h.newWorld(t)

	ev := mkEvent(t, worldID, "development.blueprint_unlocked", worldID, 1, map[string]any{
		"blueprint_id": "iron_spear",
		"recipe_id":    "iron_spear_research",
	})
	okResults(t, h.append(t, ev))
	// Re-unlock (idempotent upsert): still exactly one row.
	dup := mkEvent(t, worldID, "development.blueprint_unlocked", worldID, 2, map[string]any{
		"blueprint_id": "iron_spear",
	})
	okResults(t, h.append(t, dup))

	var n int
	if err := h.pool.QueryRow(context.Background(),
		`SELECT count(*) FROM world_blueprints WHERE world_id=$1 AND blueprint_id='iron_spear'`, worldID).Scan(&n); err != nil {
		t.Fatalf("world_blueprints lookup: %v", err)
	}
	if n != 1 {
		t.Fatalf("world_blueprints: got %d want 1", n)
	}
}

// TestMasterDataDelivery: LoadBootstrap payload carries the seeded masters and
// their values match the seed (06B DoD / 3.4). Recipe/精錬 数量・時間は 06A 3.3 と同値。
func TestMasterDataDelivery(t *testing.T) {
	h := setup(t)
	h.seedItems(t)
	worldID := h.newWorld(t)

	boot, err := h.world.LoadBootstrap(context.Background(),
		&survivalv1.LoadBootstrapRequest{WorldId: worldID, ServerBuild: "master-test"})
	if err != nil {
		t.Fatalf("LoadBootstrap: %v", err)
	}
	var md store.MasterData
	if err := json.Unmarshal(boot.GetMasterData(), &md); err != nil {
		t.Fatalf("unmarshal master_data: %v (raw=%s)", err, string(boot.GetMasterData()))
	}
	if len(md.ItemDefinitions) != 18 {
		t.Fatalf("item defs: got %d want 18", len(md.ItemDefinitions))
	}
	if len(md.ResourceNodes) != 3 {
		t.Fatalf("resource nodes: got %d want 3", len(md.ResourceNodes))
	}

	recipes := map[string]store.MasterRecipe{}
	for _, r := range md.Recipes {
		recipes[r.RecipeID] = r
	}
	// Spot-check the values that must match 06A 3.3.
	sp := recipes["stone_pickaxe"]
	if sp.CraftSeconds != 30 || sp.StationType != "anvil" || sp.OutputItem != "stone_pickaxe" {
		t.Fatalf("stone_pickaxe recipe mismatch: %+v", sp)
	}
	if !hasIngredient(sp, "stone", 5) || !hasIngredient(sp, "wood", 2) {
		t.Fatalf("stone_pickaxe ingredients mismatch: %+v", sp.Ingredients)
	}
	research := recipes["iron_spear_research"]
	if research.Kind != "development" || research.UnlockBlueprint != "iron_spear" || research.CraftSeconds != 120 {
		t.Fatalf("iron_spear_research mismatch: %+v", research)
	}
	if research.OutputItem != "" {
		t.Fatalf("development recipe should have empty output_item, got %q", research.OutputItem)
	}
	ihs := recipes["iron_hunting_spear"]
	if ihs.RequiredBlueprint != "iron_spear" || !hasIngredient(ihs, "leather", 1) {
		t.Fatalf("iron_hunting_spear mismatch: %+v", ihs)
	}
}

func hasIngredient(r store.MasterRecipe, item string, qty int) bool {
	for _, ing := range r.Ingredients {
		if ing.ItemDefinitionID == item {
			return ing.Quantity == qty
		}
	}
	return false
}

// TestCurrencyAndWeightAreIntegers: the money/weight columns are integer types
// (BIGINT / INTEGER — no floats, 13.1).
func TestCurrencyAndWeightAreIntegers(t *testing.T) {
	h := setup(t)
	cols := map[string]string{}
	rows, err := h.pool.Query(context.Background(),
		`SELECT table_name||'.'||column_name, data_type FROM information_schema.columns
		  WHERE (table_name='currency_ledger' AND column_name IN ('delta','balance_after'))
		     OR (table_name='item_definitions' AND column_name IN ('weight_milli','consume_hunger','waste_output'))`)
	if err != nil {
		t.Fatalf("query columns: %v", err)
	}
	defer rows.Close()
	for rows.Next() {
		var name, typ string
		if err := rows.Scan(&name, &typ); err != nil {
			t.Fatalf("scan: %v", err)
		}
		cols[name] = typ
	}
	want := map[string]string{
		"currency_ledger.delta":           "bigint",
		"currency_ledger.balance_after":   "bigint",
		"item_definitions.weight_milli":   "integer",
		"item_definitions.consume_hunger": "integer",
		"item_definitions.waste_output":   "integer",
	}
	for name, typ := range want {
		if cols[name] != typ {
			t.Fatalf("%s: got %q want %q (float は禁止, 13.1)", name, cols[name], typ)
		}
	}
}

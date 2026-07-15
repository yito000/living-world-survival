package store

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"

	"github.com/jackc/pgx/v5"
)

// This file applies the DB side-effects of each M3 Domain Event inside the same
// transaction that persists the event (06B 0.3 / 3.2). The API is the single
// authoritative Writer of inventory_entries / item_instances / currency_ledger /
// world_items / world_blueprints (MVP 12.2.1 / 付録C). Only completion-style
// events mutate inventory to avoid double-applying reservations (06B 0.3 注).
//
// Owner resolution (important contract note): grants/produced/drops credit a
// player's inventory, and the cleaning reward credits a player's ledger, so B
// needs the recipient's id. 06A 0.3 carries `actor_id` on station/cooking/
// consume/discard but NOT on resource.mined / carcass_butchered / crop_harvested
// / cleaning.completed. B therefore resolves the owner from owner_id/actor_id/
// killer_id when present and SKIPS the inventory/ledger mutation (recording the
// event + outbox only) when none is present. Those payloads should add an
// `actor_id` (recipient) so B can credit inventory — a required addendum to the
// 06A/06B 0.3 contract; extra JSON fields are ignored by the DS generator.

// ownerType is the inventory owner_type used for character inventories. The
// events reference actors, whose inventory is looked up by (ownerType, actorID).
const ownerType = "character"

// Default inventory capacity when an owner's inventory is created on demand
// (MVP 7: 24 slots / 40.0 weight). Weight is a milli-integer (no floats, 13.1).
const (
	defaultSlotCapacity   = 24
	defaultWeightCapacity = 40_000
)

// itemStack is one grant/consume/drop/produce entry. Stackables carry only
// item_definition_id + quantity; individuals additionally carry a DS-generated
// item_instance_id (ULID) and optional quality/durability (06A 0.3).
type itemStack struct {
	ItemDefinitionID string `json:"item_definition_id"`
	Quantity         int    `json:"quantity"`
	ItemInstanceID   string `json:"item_instance_id,omitempty"`
	Quality          int    `json:"quality,omitempty"`
	Durability       int    `json:"durability,omitempty"`
}

// vec3 is a WorldItem drop position. Coordinates are float (not currency/quantity),
// matching the REAL columns in world_items (13.1 allows floats for coordinates).
type vec3 struct {
	X float64 `json:"x"` //nolint:forbidigo // 座標は float 許容（数量/通貨ではない, 13.1）
	Y float64 `json:"y"` //nolint:forbidigo // 座標は float 許容
	Z float64 `json:"z"` //nolint:forbidigo // 座標は float 許容
}

// eventPayload is a superset of every M3 payload shape (0.3). Absent fields
// unmarshal to their zero value; only the fields relevant to a given type are read.
type eventPayload struct {
	OwnerID  string `json:"owner_id"`
	ActorID  string `json:"actor_id"`
	KillerID string `json:"killer_id"`

	Grants   []itemStack `json:"grants"`
	Produced []itemStack `json:"produced"`
	Drops    []itemStack `json:"drops"`
	Consumed []itemStack `json:"consumed"`

	// inventory.item_consumed / item.discarded single-item fields.
	ItemDefinitionID string `json:"item_definition_id"`
	ItemInstanceID   string `json:"item_instance_id"`
	Quantity         int    `json:"quantity"`

	// item.discarded
	WorldItemID string   `json:"world_item_id"`
	Position    *vec3    `json:"position"`
	Tags        []string `json:"tags"`

	// cleaning.completed
	RewardAmount int64 `json:"reward_amount"`

	// development.blueprint_unlocked
	BlueprintID string `json:"blueprint_id"`
}

// owner returns the resolved inventory/ledger owner id, or "" when the payload
// carries none (see contract note above).
func (p *eventPayload) owner() string {
	switch {
	case p.OwnerID != "":
		return p.OwnerID
	case p.ActorID != "":
		return p.ActorID
	case p.KillerID != "":
		return p.KillerID
	default:
		return ""
	}
}

// applyEffectTx applies a single event's DB side-effects within tx (called from
// appendOneTx after the domain_events row is inserted). Unknown or record-only
// types are no-ops. A nil error means the effect (if any) was fully applied.
func applyEffectTx(ctx context.Context, tx pgx.Tx, e EventInput) error {
	var p eventPayload
	if len(e.Payload) > 0 {
		if err := json.Unmarshal(e.Payload, &p); err != nil {
			// A payload we cannot parse must not silently drop its effect: fail the
			// event so the DS can resync rather than persist a half-applied state.
			return fmt.Errorf("store: apply %s: bad payload: %w", e.Type, err)
		}
	}

	switch e.Type {
	case "resource.mined":
		return grantAll(ctx, tx, p.owner(), p.Grants)
	case "hunting.carcass_butchered":
		return grantAll(ctx, tx, p.owner(), p.Drops)
	case "farm.crop_harvested":
		return grantAll(ctx, tx, p.owner(), p.Produced)
	case "station.job_completed", "cooking.completed":
		if err := consumeAll(ctx, tx, p.owner(), p.Consumed); err != nil {
			return err
		}
		return grantAll(ctx, tx, p.owner(), p.Produced)
	case "inventory.item_consumed":
		return consumeAll(ctx, tx, p.owner(), []itemStack{{
			ItemDefinitionID: p.ItemDefinitionID,
			ItemInstanceID:   p.ItemInstanceID,
			Quantity:         defaultQty(p.Quantity),
		}})
	case "item.discarded":
		return applyDiscard(ctx, tx, e.WorldID, &p)
	case "cleaning.completed":
		return applyCleaning(ctx, tx, e.WorldID, &p)
	case "development.blueprint_unlocked":
		if p.BlueprintID == "" {
			return nil
		}
		return upsertBlueprintTx(ctx, tx, e.WorldID, p.BlueprintID)
	default:
		// resource.node_* / hunting.animal_killed / station.job_started|cancelled /
		// farm.crop_planted / character.vitals_changed: record + NATS only (0.3 注).
		return nil
	}
}

func defaultQty(q int) int {
	if q <= 0 {
		return 1
	}
	return q
}

// grantAll adds each stack/instance to the owner's inventory. A missing owner is
// a no-op (record-only) per the contract note.
func grantAll(ctx context.Context, tx pgx.Tx, owner string, items []itemStack) error {
	if owner == "" || len(items) == 0 {
		return nil
	}
	invID, err := ensureInventoryTx(ctx, tx, owner)
	if err != nil {
		return err
	}
	for _, it := range items {
		if it.ItemDefinitionID == "" {
			continue
		}
		def, err := itemDefEffectTx(ctx, tx, it.ItemDefinitionID)
		if err != nil {
			return err
		}
		if it.ItemInstanceID != "" || def.IsInstance {
			if err := addInstanceTx(ctx, tx, invID, it); err != nil {
				return err
			}
			continue
		}
		if err := addStackTx(ctx, tx, invID, it.ItemDefinitionID, defaultQty(it.Quantity), def.StackLimit); err != nil {
			return err
		}
	}
	return bumpInventoryVersionTx(ctx, tx, invID)
}

// consumeAll removes each stack/instance from the owner's inventory.
func consumeAll(ctx context.Context, tx pgx.Tx, owner string, items []itemStack) error {
	if owner == "" || len(items) == 0 {
		return nil
	}
	invID, err := ensureInventoryTx(ctx, tx, owner)
	if err != nil {
		return err
	}
	for _, it := range items {
		if it.ItemInstanceID != "" {
			if err := removeInstanceTx(ctx, tx, invID, it.ItemInstanceID); err != nil {
				return err
			}
			continue
		}
		if it.ItemDefinitionID == "" {
			continue
		}
		if err := removeStackTx(ctx, tx, invID, it.ItemDefinitionID, defaultQty(it.Quantity)); err != nil {
			return err
		}
	}
	return bumpInventoryVersionTx(ctx, tx, invID)
}

// applyDiscard removes the discarded item from the owner's inventory and records
// it as a persistent WorldItem so it survives reconnection (AT-010).
func applyDiscard(ctx context.Context, tx pgx.Tx, worldID string, p *eventPayload) error {
	owner := p.owner()
	qty := defaultQty(p.Quantity)
	if owner != "" && (p.ItemDefinitionID != "" || p.ItemInstanceID != "") {
		invID, err := ensureInventoryTx(ctx, tx, owner)
		if err != nil {
			return err
		}
		if p.ItemInstanceID != "" {
			if err := removeInstanceTx(ctx, tx, invID, p.ItemInstanceID); err != nil {
				return err
			}
		} else if err := removeStackTx(ctx, tx, invID, p.ItemDefinitionID, qty); err != nil {
			return err
		}
		if err := bumpInventoryVersionTx(ctx, tx, invID); err != nil {
			return err
		}
	}
	if p.WorldItemID == "" {
		return nil
	}
	var x, y, z float64 //nolint:forbidigo // 座標は float 許容（数量/通貨ではない, 13.1）
	if p.Position != nil {
		x, y, z = p.Position.X, p.Position.Y, p.Position.Z
	}
	var instanceID *string
	if p.ItemInstanceID != "" {
		instanceID = &p.ItemInstanceID
	}
	var ownerPtr *string
	if owner != "" {
		ownerPtr = &owner
	}
	tags := p.Tags
	if tags == nil {
		tags = []string{} // world_items.tags is NOT NULL; a nil slice would insert NULL.
	}
	_, err := tx.Exec(ctx,
		`INSERT INTO world_items
		   (world_item_id, world_id, item_definition_id, item_instance_id, quantity, pos_x, pos_y, pos_z, owner_id, tags)
		 VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
		 ON CONFLICT (world_item_id) DO NOTHING`,
		p.WorldItemID, worldID, p.ItemDefinitionID, instanceID, qty, x, y, z, ownerPtr, tags,
	)
	if err != nil {
		return fmt.Errorf("store: insert world_item: %w", err)
	}
	return nil
}

// applyCleaning removes the cleaned WorldItem (Disposal) and, when a positive
// reward is present and an owner is resolvable, appends a currency ledger entry
// (BIGINT, integer money only — 13.1).
func applyCleaning(ctx context.Context, tx pgx.Tx, worldID string, p *eventPayload) error {
	if p.WorldItemID != "" {
		if _, err := tx.Exec(ctx, `DELETE FROM world_items WHERE world_item_id = $1`, p.WorldItemID); err != nil {
			return fmt.Errorf("store: delete world_item: %w", err)
		}
	}
	owner := p.owner()
	if p.RewardAmount <= 0 || owner == "" {
		return nil
	}
	return appendCurrencyTx(ctx, tx, owner, p.RewardAmount, "cleaning_reward")
}

// --- inventory primitives (tx-scoped) --------------------------------------

func ensureInventoryTx(ctx context.Context, tx pgx.Tx, ownerID string) (string, error) {
	invID := NewUUID()
	err := tx.QueryRow(ctx,
		`INSERT INTO inventories (inventory_id, owner_type, owner_id, slot_capacity, weight_capacity)
		 VALUES ($1, $2, $3, $4, $5)
		 ON CONFLICT (owner_type, owner_id) DO UPDATE SET owner_id = EXCLUDED.owner_id
		 RETURNING inventory_id::text`,
		invID, ownerType, ownerID, defaultSlotCapacity, defaultWeightCapacity,
	).Scan(&invID)
	if err != nil {
		return "", fmt.Errorf("store: ensure inventory: %w", err)
	}
	return invID, nil
}

func bumpInventoryVersionTx(ctx context.Context, tx pgx.Tx, invID string) error {
	if _, err := tx.Exec(ctx,
		`UPDATE inventories SET version = version + 1 WHERE inventory_id = $1`, invID); err != nil {
		return fmt.Errorf("store: bump inventory version: %w", err)
	}
	return nil
}

func itemDefEffectTx(ctx context.Context, tx pgx.Tx, itemDefID string) (itemDefEffect, error) {
	var d itemDefEffect
	err := tx.QueryRow(ctx,
		`SELECT stack_limit, is_instance FROM item_definitions WHERE item_definition_id = $1`, itemDefID,
	).Scan(&d.StackLimit, &d.IsInstance)
	if errors.Is(err, pgx.ErrNoRows) {
		// Unknown item: treat as a non-stacking single so we never divide by a zero
		// stack limit. DS master validation should prevent this in practice.
		return itemDefEffect{StackLimit: 1, IsInstance: false}, nil
	}
	if err != nil {
		return d, fmt.Errorf("store: item def effect %s: %w", itemDefID, err)
	}
	if d.StackLimit <= 0 {
		d.StackLimit = 1
	}
	return d, nil
}

// addStackTx adds qty of a stackable item, topping up existing partial stacks
// (same item, no instance) before allocating new slots, each capped at stackLimit.
func addStackTx(ctx context.Context, tx pgx.Tx, invID, itemDefID string, qty, stackLimit int) error {
	if qty <= 0 {
		return nil
	}
	remaining := qty
	// 1) Top up existing partial stacks.
	rows, err := tx.Query(ctx,
		`SELECT slot_index, quantity FROM inventory_entries
		  WHERE inventory_id = $1 AND item_definition_id = $2 AND item_instance_id IS NULL
		    AND quantity < $3
		  ORDER BY slot_index ASC`,
		invID, itemDefID, stackLimit,
	)
	if err != nil {
		return fmt.Errorf("store: scan partial stacks: %w", err)
	}
	type slotQty struct {
		slot int
		qty  int
	}
	var partials []slotQty
	for rows.Next() {
		var sq slotQty
		if err := rows.Scan(&sq.slot, &sq.qty); err != nil {
			rows.Close()
			return fmt.Errorf("store: scan partial: %w", err)
		}
		partials = append(partials, sq)
	}
	rows.Close()
	if err := rows.Err(); err != nil {
		return err
	}
	for _, sq := range partials {
		if remaining <= 0 {
			break
		}
		room := stackLimit - sq.qty
		if room <= 0 {
			continue
		}
		add := room
		if add > remaining {
			add = remaining
		}
		if _, err := tx.Exec(ctx,
			`UPDATE inventory_entries SET quantity = quantity + $3
			  WHERE inventory_id = $1 AND slot_index = $2`,
			invID, sq.slot, add); err != nil {
			return fmt.Errorf("store: top up stack: %w", err)
		}
		remaining -= add
	}
	// 2) Allocate new slots for the remainder.
	for remaining > 0 {
		slot, err := nextFreeSlotTx(ctx, tx, invID)
		if err != nil {
			return err
		}
		add := stackLimit
		if add > remaining {
			add = remaining
		}
		if _, err := tx.Exec(ctx,
			`INSERT INTO inventory_entries (inventory_id, slot_index, item_definition_id, quantity)
			 VALUES ($1, $2, $3, $4)`,
			invID, slot, itemDefID, add); err != nil {
			return fmt.Errorf("store: insert stack slot: %w", err)
		}
		remaining -= add
	}
	return nil
}

// addInstanceTx inserts the item_instances row (if new) and places the individual
// into a fresh slot with quantity 1.
func addInstanceTx(ctx context.Context, tx pgx.Tx, invID string, it itemStack) error {
	instanceID := it.ItemInstanceID
	if instanceID == "" {
		instanceID = NewUUID()
	}
	if _, err := tx.Exec(ctx,
		`INSERT INTO item_instances (item_instance_id, definition_id, quality, durability)
		 VALUES ($1, $2, $3, $4)
		 ON CONFLICT (item_instance_id) DO NOTHING`,
		instanceID, it.ItemDefinitionID, it.Quality, it.Durability); err != nil {
		return fmt.Errorf("store: insert item instance: %w", err)
	}
	slot, err := nextFreeSlotTx(ctx, tx, invID)
	if err != nil {
		return err
	}
	if _, err := tx.Exec(ctx,
		`INSERT INTO inventory_entries (inventory_id, slot_index, item_definition_id, item_instance_id, quantity)
		 VALUES ($1, $2, $3, $4, 1)`,
		invID, slot, it.ItemDefinitionID, instanceID); err != nil {
		return fmt.Errorf("store: insert instance slot: %w", err)
	}
	return nil
}

// removeStackTx decrements qty of a stackable across slots (lowest slot first),
// deleting emptied rows. Missing quantity is tolerated (DS guarantees via
// reserved) — it removes what is available.
func removeStackTx(ctx context.Context, tx pgx.Tx, invID, itemDefID string, qty int) error {
	if qty <= 0 {
		return nil
	}
	remaining := qty
	rows, err := tx.Query(ctx,
		`SELECT slot_index, quantity FROM inventory_entries
		  WHERE inventory_id = $1 AND item_definition_id = $2 AND item_instance_id IS NULL
		  ORDER BY slot_index ASC`,
		invID, itemDefID,
	)
	if err != nil {
		return fmt.Errorf("store: scan for remove: %w", err)
	}
	type slotQty struct {
		slot int
		qty  int
	}
	var slots []slotQty
	for rows.Next() {
		var sq slotQty
		if err := rows.Scan(&sq.slot, &sq.qty); err != nil {
			rows.Close()
			return fmt.Errorf("store: scan remove: %w", err)
		}
		slots = append(slots, sq)
	}
	rows.Close()
	if err := rows.Err(); err != nil {
		return err
	}
	for _, sq := range slots {
		if remaining <= 0 {
			break
		}
		if sq.qty <= remaining {
			if _, err := tx.Exec(ctx,
				`DELETE FROM inventory_entries WHERE inventory_id = $1 AND slot_index = $2`,
				invID, sq.slot); err != nil {
				return fmt.Errorf("store: delete emptied slot: %w", err)
			}
			remaining -= sq.qty
		} else {
			if _, err := tx.Exec(ctx,
				`UPDATE inventory_entries SET quantity = quantity - $3
				  WHERE inventory_id = $1 AND slot_index = $2`,
				invID, sq.slot, remaining); err != nil {
				return fmt.Errorf("store: decrement slot: %w", err)
			}
			remaining = 0
		}
	}
	return nil
}

func removeInstanceTx(ctx context.Context, tx pgx.Tx, invID, instanceID string) error {
	if _, err := tx.Exec(ctx,
		`DELETE FROM inventory_entries WHERE inventory_id = $1 AND item_instance_id = $2`,
		invID, instanceID); err != nil {
		return fmt.Errorf("store: remove instance slot: %w", err)
	}
	return nil
}

// nextFreeSlotTx returns the lowest non-negative slot index not currently used
// by the inventory.
func nextFreeSlotTx(ctx context.Context, tx pgx.Tx, invID string) (int, error) {
	// The smallest gap: the lowest s>=0 where s is not present.
	var slot int
	err := tx.QueryRow(ctx,
		`SELECT coalesce(min(s), 0) FROM generate_series(0, (
		    SELECT coalesce(max(slot_index), -1) + 1 FROM inventory_entries WHERE inventory_id = $1
		 )) AS s
		 WHERE s NOT IN (SELECT slot_index FROM inventory_entries WHERE inventory_id = $1)`,
		invID,
	).Scan(&slot)
	if err != nil {
		return 0, fmt.Errorf("store: next free slot: %w", err)
	}
	return slot, nil
}

func upsertBlueprintTx(ctx context.Context, tx pgx.Tx, worldID, blueprintID string) error {
	if _, err := tx.Exec(ctx,
		`INSERT INTO world_blueprints (world_id, blueprint_id) VALUES ($1, $2)
		 ON CONFLICT (world_id, blueprint_id) DO NOTHING`,
		worldID, blueprintID); err != nil {
		return fmt.Errorf("store: upsert blueprint: %w", err)
	}
	return nil
}

// appendCurrencyTx appends a ledger entry, computing balance_after from the
// owner's latest entry (BIGINT integer money, 13.1).
func appendCurrencyTx(ctx context.Context, tx pgx.Tx, ownerID string, delta int64, reason string) error {
	var prev int64
	if err := tx.QueryRow(ctx,
		`SELECT coalesce((
		    SELECT balance_after FROM currency_ledger
		     WHERE owner_id = $1 ORDER BY created_at DESC, entry_id DESC LIMIT 1), 0)`,
		ownerID,
	).Scan(&prev); err != nil {
		return fmt.Errorf("store: currency balance: %w", err)
	}
	if _, err := tx.Exec(ctx,
		`INSERT INTO currency_ledger (entry_id, owner_id, delta, balance_after, reason)
		 VALUES ($1, $2, $3, $4, $5)`,
		NewUUID(), ownerID, delta, prev+delta, reason,
	); err != nil {
		return fmt.Errorf("store: insert currency ledger: %w", err)
	}
	return nil
}

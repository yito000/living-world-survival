package integration

import (
	"context"
	"encoding/json"
	"fmt"
	"sync"
	"testing"

	"living-world-survival/services/api/internal/economy"
	"living-world-survival/services/api/internal/itemdef"
	"living-world-survival/services/api/internal/store"
	survivalv1 "living-world-survival/services/gen/go/survival/v1"
)

// M6 Economy 統合テスト（09B 5章）。実 PostgreSQL に対して購入/売却の単一 Tx を
// 検証する。DB が無い環境では setup が Skip する。

const testTableID = "rare_weapon_buyer_v1"

type economyHarness struct {
	*harness
	server  *economy.Server
	catalog *itemdef.Catalog
}

func economySetup(t *testing.T) *economyHarness {
	t.Helper()
	h := setup(t)

	catalog, err := itemdef.Load("../../data/item_definitions.json")
	if err != nil {
		t.Fatalf("load item definitions: %v", err)
	}
	// 価格（base_value / sell_price）は item_definitions 由来なので、購入 Tx が
	// 参照できるようマスタを seed しておく。
	if err := catalog.Seed(context.Background(), h.store); err != nil {
		t.Fatalf("seed item definitions: %v", err)
	}
	server, err := economy.NewServer(h.store, catalog)
	if err != nil {
		t.Fatalf("economy server: %v", err)
	}
	return &economyHarness{harness: h, server: server, catalog: catalog}
}

// registerBuyer registers a Buyer with the given seed and returns it with stock.
func (h *economyHarness) registerBuyer(t *testing.T, worldID string, seed int64) *survivalv1.RegisterBuyerResponse {
	t.Helper()
	resp, err := h.server.RegisterBuyer(context.Background(), &survivalv1.RegisterBuyerRequest{
		IdempotencyKey:   "buyer-" + store.NewUUID(),
		WorldId:          worldID,
		RegionId:         "region-1",
		Seed:             seed,
		InventoryTableId: testTableID,
		PriceModifierBp:  10000,
		SpawnAtUnixMs:    1_700_000_000_000,
		DespawnAtUnixMs:  1_700_000_600_000,
	})
	if err != nil {
		t.Fatalf("RegisterBuyer: %v", err)
	}
	if len(resp.GetStock()) == 0 {
		t.Fatal("RegisterBuyer returned no stock")
	}
	return resp
}

// credit gives an owner a starting balance via the ledger.
func (h *economyHarness) credit(t *testing.T, ownerID string, amount int64) {
	t.Helper()
	_, err := h.pool.Exec(context.Background(),
		`INSERT INTO currency_ledger (entry_id, owner_id, delta, balance_after, reason)
		 VALUES ($1, $2, $3, $3, 'test_seed')`,
		store.NewUUID(), ownerID, amount)
	if err != nil {
		t.Fatalf("credit: %v", err)
	}
}

func (h *economyHarness) balance(t *testing.T, ownerID string) int64 {
	t.Helper()
	var b int64
	err := h.pool.QueryRow(context.Background(),
		`SELECT coalesce((SELECT balance_after FROM currency_ledger
		   WHERE owner_id = $1 ORDER BY created_at DESC, entry_id DESC LIMIT 1), 0)`,
		ownerID).Scan(&b)
	if err != nil {
		t.Fatalf("balance: %v", err)
	}
	return b
}

func (h *economyHarness) remaining(t *testing.T, stockEntryID string) int {
	t.Helper()
	var n int
	if err := h.pool.QueryRow(context.Background(),
		`SELECT remaining_quantity FROM buyer_stock WHERE stock_entry_id = $1`, stockEntryID).Scan(&n); err != nil {
		t.Fatalf("remaining: %v", err)
	}
	return n
}

// setRemaining forces a stock entry's quantity so 枯渇 can be tested precisely.
func (h *economyHarness) setRemaining(t *testing.T, stockEntryID string, qty int) {
	t.Helper()
	if _, err := h.pool.Exec(context.Background(),
		`UPDATE buyer_stock SET remaining_quantity = $2 WHERE stock_entry_id = $1`,
		stockEntryID, qty); err != nil {
		t.Fatalf("setRemaining: %v", err)
	}
}

func (h *economyHarness) purchase(t *testing.T, key, buyerID, stockID, purchaser string, invVersion int64) *survivalv1.CommitPurchaseResponse {
	t.Helper()
	resp, err := h.server.CommitPurchase(context.Background(), &survivalv1.CommitPurchaseRequest{
		IdempotencyKey:   key,
		BuyerInstanceId:  buyerID,
		StockEntryId:     stockID,
		PurchaserId:      purchaser,
		InventoryVersion: invVersion,
	})
	if err != nil {
		t.Fatalf("CommitPurchase: %v", err)
	}
	return resp
}

// economyEvents returns the outbox payloads enqueued for a world's economy
// subject (14.3). The relay publishes these to NATS asynchronously.
func (h *economyHarness) economyEvents(t *testing.T, worldID string) []map[string]any {
	t.Helper()
	rows, err := h.pool.Query(context.Background(),
		`SELECT payload FROM outbox_messages WHERE topic = $1 ORDER BY available_at ASC`,
		"world."+worldID+".event.economy")
	if err != nil {
		t.Fatalf("read outbox: %v", err)
	}
	defer rows.Close()

	var out []map[string]any
	for rows.Next() {
		var raw []byte
		if err := rows.Scan(&raw); err != nil {
			t.Fatalf("scan outbox: %v", err)
		}
		var m map[string]any
		if err := json.Unmarshal(raw, &m); err != nil {
			t.Fatalf("unmarshal payload: %v", err)
		}
		out = append(out, m)
	}
	return out
}

// --- 冪等性 (AT-019) --------------------------------------------------------

func TestCommitPurchaseIdempotent(t *testing.T) {
	h := economySetup(t)
	worldID := h.newWorld(t)
	buyer := h.registerBuyer(t, worldID, 1)
	entry := buyer.GetStock()[0]
	purchaser := store.NewUUID()
	h.credit(t, purchaser, 1_000_000)

	key := "idem-" + store.NewUUID()
	before := h.remaining(t, entry.GetStockEntryId())

	first := h.purchase(t, key, buyer.GetBuyerInstanceId(), entry.GetStockEntryId(), purchaser, 0)
	if first.GetStatus() != survivalv1.PurchaseStatus_PURCHASE_STATUS_COMMITTED {
		t.Fatalf("1回目: got %v want COMMITTED", first.GetStatus())
	}
	balanceAfterFirst := h.balance(t, purchaser)

	// 同一 idempotency_key の再送 → DUPLICATE、以前の結果を返す。
	second := h.purchase(t, key, buyer.GetBuyerInstanceId(), entry.GetStockEntryId(), purchaser, 0)
	if second.GetStatus() != survivalv1.PurchaseStatus_PURCHASE_STATUS_DUPLICATE {
		t.Fatalf("2回目: got %v want DUPLICATE", second.GetStatus())
	}

	// 返却は同一結果（AT-019: 応答を完全復元できること）。
	if second.GetCharged().GetAmount() != first.GetCharged().GetAmount() {
		t.Errorf("charged: 再送 %d != 初回 %d", second.GetCharged().GetAmount(), first.GetCharged().GetAmount())
	}
	if second.GetNewPersistedInventoryVersion() != first.GetNewPersistedInventoryVersion() {
		t.Errorf("inventory_version: 再送 %d != 初回 %d",
			second.GetNewPersistedInventoryVersion(), first.GetNewPersistedInventoryVersion())
	}
	if len(second.GetItemInstanceIds()) != len(first.GetItemInstanceIds()) {
		t.Errorf("item_instance_ids の数が違う: %v vs %v", second.GetItemInstanceIds(), first.GetItemInstanceIds())
	}
	for i := range first.GetItemInstanceIds() {
		if second.GetItemInstanceIds()[i] != first.GetItemInstanceIds()[i] {
			t.Errorf("item_instance_ids[%d]: 再送 %s != 初回 %s", i, second.GetItemInstanceIds()[i], first.GetItemInstanceIds()[i])
		}
	}

	// 台帳・在庫は 1 回分しか動いていない。
	if got := h.balance(t, purchaser); got != balanceAfterFirst {
		t.Errorf("再送で残高が動いた: %d → %d", balanceAfterFirst, got)
	}
	if got := h.remaining(t, entry.GetStockEntryId()); got != before-1 {
		t.Errorf("再送で在庫が二重に減った: %d → %d (want %d)", before, got, before-1)
	}
	var txCount int
	if err := h.pool.QueryRow(context.Background(),
		`SELECT count(*) FROM purchase_transactions WHERE idempotency_key = $1`, key).Scan(&txCount); err != nil {
		t.Fatal(err)
	}
	if txCount != 1 {
		t.Errorf("purchase_transactions 行数 = %d, want 1", txCount)
	}
}

// --- 在庫枯渇 (AT-011 / AT-012) --------------------------------------------

func TestCommitPurchaseOutOfStock(t *testing.T) {
	h := economySetup(t)
	worldID := h.newWorld(t)
	buyer := h.registerBuyer(t, worldID, 2)
	entry := buyer.GetStock()[0]
	h.setRemaining(t, entry.GetStockEntryId(), 1)

	purchaser := store.NewUUID()
	h.credit(t, purchaser, 1_000_000)

	first := h.purchase(t, "k1-"+store.NewUUID(), buyer.GetBuyerInstanceId(), entry.GetStockEntryId(), purchaser, 0)
	if first.GetStatus() != survivalv1.PurchaseStatus_PURCHASE_STATUS_COMMITTED {
		t.Fatalf("1回目: got %v want COMMITTED", first.GetStatus())
	}
	balanceAfterFirst := h.balance(t, purchaser)

	second := h.purchase(t, "k2-"+store.NewUUID(), buyer.GetBuyerInstanceId(), entry.GetStockEntryId(), purchaser, 1)
	if second.GetStatus() != survivalv1.PurchaseStatus_PURCHASE_STATUS_OUT_OF_STOCK {
		t.Fatalf("2回目: got %v want OUT_OF_STOCK", second.GetStatus())
	}
	if got := h.remaining(t, entry.GetStockEntryId()); got != 0 {
		t.Errorf("在庫が負に落ちた: %d", got)
	}
	if got := h.balance(t, purchaser); got != balanceAfterFirst {
		t.Errorf("枯渇時に残高が動いた: %d → %d", balanceAfterFirst, got)
	}
}

// --- 残高不足 (12.2) --------------------------------------------------------

func TestCommitPurchaseInsufficientFunds(t *testing.T) {
	h := economySetup(t)
	worldID := h.newWorld(t)
	buyer := h.registerBuyer(t, worldID, 3)

	// unit_price より確実に安い残高で臨む。
	entry := buyer.GetStock()[0]
	purchaser := store.NewUUID()
	h.credit(t, purchaser, entry.GetUnitPrice()-1)

	before := h.remaining(t, entry.GetStockEntryId())
	resp := h.purchase(t, "k-"+store.NewUUID(), buyer.GetBuyerInstanceId(), entry.GetStockEntryId(), purchaser, 0)
	if resp.GetStatus() != survivalv1.PurchaseStatus_PURCHASE_STATUS_INSUFFICIENT_FUNDS {
		t.Fatalf("got %v want INSUFFICIENT_FUNDS (unit_price=%d)", resp.GetStatus(), entry.GetUnitPrice())
	}
	// 在庫・台帳を動かさない。
	if got := h.remaining(t, entry.GetStockEntryId()); got != before {
		t.Errorf("残高不足で在庫が動いた: %d → %d", before, got)
	}
	if got := h.balance(t, purchaser); got != entry.GetUnitPrice()-1 {
		t.Errorf("残高不足で台帳が動いた: %d", got)
	}
}

// --- 同時購入 (AT-012) ------------------------------------------------------

func TestCommitPurchaseConcurrent(t *testing.T) {
	h := economySetup(t)
	worldID := h.newWorld(t)
	buyer := h.registerBuyer(t, worldID, 4)
	entry := buyer.GetStock()[0]
	h.setRemaining(t, entry.GetStockEntryId(), 1)

	// 別々の購入者が同一 stock_entry_id を同時に買う → 1 者のみ成功。
	buyerA, buyerB := store.NewUUID(), store.NewUUID()
	h.credit(t, buyerA, 1_000_000)
	h.credit(t, buyerB, 1_000_000)

	var wg sync.WaitGroup
	results := make([]survivalv1.PurchaseStatus, 2)
	for i, p := range []string{buyerA, buyerB} {
		wg.Add(1)
		go func(i int, purchaser string) {
			defer wg.Done()
			resp, err := h.server.CommitPurchase(context.Background(), &survivalv1.CommitPurchaseRequest{
				IdempotencyKey:   "conc-" + store.NewUUID(),
				BuyerInstanceId:  buyer.GetBuyerInstanceId(),
				StockEntryId:     entry.GetStockEntryId(),
				PurchaserId:      purchaser,
				InventoryVersion: 0,
			})
			if err != nil {
				t.Errorf("CommitPurchase: %v", err)
				return
			}
			results[i] = resp.GetStatus()
		}(i, p)
	}
	wg.Wait()

	committed := 0
	for _, r := range results {
		if r == survivalv1.PurchaseStatus_PURCHASE_STATUS_COMMITTED {
			committed++
		}
	}
	if committed != 1 {
		t.Fatalf("同時購入で %d 件成功した（want 1）: %v", committed, results)
	}
	if got := h.remaining(t, entry.GetStockEntryId()); got != 0 {
		t.Errorf("在庫の不整合: remaining = %d, want 0", got)
	}

	// 成功した側だけが課金され、Item も 1 個だけ払い出されている。
	spentA := 1_000_000 - h.balance(t, buyerA)
	spentB := 1_000_000 - h.balance(t, buyerB)
	if spentA+spentB != entry.GetUnitPrice() {
		t.Errorf("課金合計 %d != unit_price %d（二重課金/取りこぼし）", spentA+spentB, entry.GetUnitPrice())
	}
	var granted int
	if err := h.pool.QueryRow(context.Background(),
		`SELECT count(*) FROM purchase_transactions WHERE stock_entry_id = $1 AND status = 'committed'`,
		entry.GetStockEntryId()).Scan(&granted); err != nil {
		t.Fatal(err)
	}
	if granted != 1 {
		t.Errorf("確定した取引が %d 件（want 1）", granted)
	}
}

// 同一購入者の並行購入で残高を二重使用しないこと（MVP-SEC-009 口座の直列化）。
func TestCommitPurchaseSerializesAccount(t *testing.T) {
	h := economySetup(t)
	worldID := h.newWorld(t)
	buyer := h.registerBuyer(t, worldID, 5)

	// 1 個だけ買える残高で、別 slot を 2 つ同時に買いにいく。
	a, b := buyer.GetStock()[0], buyer.GetStock()[1%len(buyer.GetStock())]
	if a.GetStockEntryId() == b.GetStockEntryId() {
		t.Skip("この seed では slot が 1 つしかない")
	}
	price := a.GetUnitPrice()
	if b.GetUnitPrice() > price {
		price = b.GetUnitPrice()
	}
	purchaser := store.NewUUID()
	h.credit(t, purchaser, price) // ちょうど 1 個分

	var wg sync.WaitGroup
	statuses := make([]survivalv1.PurchaseStatus, 2)
	for i, e := range []*survivalv1.BuyerStockEntry{a, b} {
		wg.Add(1)
		go func(i int, stockID string) {
			defer wg.Done()
			resp, err := h.server.CommitPurchase(context.Background(), &survivalv1.CommitPurchaseRequest{
				IdempotencyKey:  "acct-" + store.NewUUID(),
				BuyerInstanceId: buyer.GetBuyerInstanceId(),
				StockEntryId:    stockID,
				PurchaserId:     purchaser,
				// version は直列化の結果どちらが先でも良いよう -1 を避け、
				// 実際の version と一致する 0 / 1 のどちらかになる。
				InventoryVersion: int64(i),
			})
			if err != nil {
				t.Errorf("CommitPurchase: %v", err)
				return
			}
			statuses[i] = resp.GetStatus()
		}(i, e.GetStockEntryId())
	}
	wg.Wait()

	// 口座が直列化されていれば、残高が負になることは決してない。
	if got := h.balance(t, purchaser); got < 0 {
		t.Fatalf("残高が負になった（二重使用）: %d — statuses=%v", got, statuses)
	}
}

// --- Buyer 非 active (12.2) -------------------------------------------------

func TestCommitPurchaseRejectedWhenBuyerNotActive(t *testing.T) {
	h := economySetup(t)
	worldID := h.newWorld(t)
	buyer := h.registerBuyer(t, worldID, 6)
	entry := buyer.GetStock()[0]
	purchaser := store.NewUUID()
	h.credit(t, purchaser, 1_000_000)

	// despawn 準備 → 新規購入は拒否。
	resp, err := h.server.DespawnBuyer(context.Background(), &survivalv1.DespawnBuyerRequest{
		BuyerInstanceId: buyer.GetBuyerInstanceId(),
		TargetStatus:    "PREPARING",
	})
	if err != nil {
		t.Fatalf("DespawnBuyer: %v", err)
	}
	if resp.GetStatus() != survivalv1.ResultStatus_RESULT_STATUS_OK {
		t.Fatalf("DespawnBuyer: got %v want OK", resp.GetStatus())
	}

	before := h.remaining(t, entry.GetStockEntryId())
	got := h.purchase(t, "k-"+store.NewUUID(), buyer.GetBuyerInstanceId(), entry.GetStockEntryId(), purchaser, 0)
	if got.GetStatus() != survivalv1.PurchaseStatus_PURCHASE_STATUS_REJECTED {
		t.Fatalf("preparing の Buyer から購入: got %v want REJECTED", got.GetStatus())
	}
	if after := h.remaining(t, entry.GetStockEntryId()); after != before {
		t.Errorf("拒否したのに在庫が動いた: %d → %d", before, after)
	}
}

// --- version 不一致 (12.2.1) ------------------------------------------------

func TestCommitPurchaseInventoryVersionMismatch(t *testing.T) {
	h := economySetup(t)
	worldID := h.newWorld(t)
	buyer := h.registerBuyer(t, worldID, 7)
	entry := buyer.GetStock()[0]
	purchaser := store.NewUUID()
	h.credit(t, purchaser, 1_000_000)

	before := h.remaining(t, entry.GetStockEntryId())
	resp := h.purchase(t, "k-"+store.NewUUID(), buyer.GetBuyerInstanceId(), entry.GetStockEntryId(), purchaser, 999)
	if resp.GetStatus() != survivalv1.PurchaseStatus_PURCHASE_STATUS_REJECTED {
		t.Fatalf("version 不一致: got %v want REJECTED", resp.GetStatus())
	}
	if after := h.remaining(t, entry.GetStockEntryId()); after != before {
		t.Errorf("拒否したのに在庫が動いた: %d → %d", before, after)
	}
}

// --- 存在しない stock ------------------------------------------------------

func TestCommitPurchaseUnknownStock(t *testing.T) {
	h := economySetup(t)
	worldID := h.newWorld(t)
	buyer := h.registerBuyer(t, worldID, 8)
	purchaser := store.NewUUID()
	h.credit(t, purchaser, 1_000_000)

	resp := h.purchase(t, "k-"+store.NewUUID(), buyer.GetBuyerInstanceId(), store.NewUUID(), purchaser, 0)
	if resp.GetStatus() != survivalv1.PurchaseStatus_PURCHASE_STATUS_REJECTED {
		t.Fatalf("未知の stock: got %v want REJECTED", resp.GetStatus())
	}
}

// --- RegisterBuyer の冪等・決定性 (AT-011) ---------------------------------

func TestRegisterBuyerIdempotentAndDeterministic(t *testing.T) {
	h := economySetup(t)
	worldID := h.newWorld(t)
	key := "buyer-" + store.NewUUID()

	req := &survivalv1.RegisterBuyerRequest{
		IdempotencyKey:   key,
		WorldId:          worldID,
		RegionId:         "region-1",
		Seed:             12345,
		InventoryTableId: testTableID,
		PriceModifierBp:  10000,
		SpawnAtUnixMs:    1_700_000_000_000,
		DespawnAtUnixMs:  1_700_000_600_000,
	}
	first, err := h.server.RegisterBuyer(context.Background(), req)
	if err != nil {
		t.Fatalf("RegisterBuyer #1: %v", err)
	}
	second, err := h.server.RegisterBuyer(context.Background(), req)
	if err != nil {
		t.Fatalf("RegisterBuyer #2: %v", err)
	}
	// 冪等: 同じ Buyer / 同じ在庫が返り、行は増えない。
	if first.GetBuyerInstanceId() != second.GetBuyerInstanceId() {
		t.Errorf("再送で別 Buyer が生えた: %s vs %s", first.GetBuyerInstanceId(), second.GetBuyerInstanceId())
	}
	var stockRows int
	if err := h.pool.QueryRow(context.Background(),
		`SELECT count(*) FROM buyer_stock WHERE buyer_instance_id = $1`,
		first.GetBuyerInstanceId()).Scan(&stockRows); err != nil {
		t.Fatal(err)
	}
	if stockRows != len(first.GetStock()) {
		t.Errorf("在庫行が二重生成された: %d 行 / 返却 %d 件", stockRows, len(first.GetStock()))
	}

	// 決定性: 別 Buyer でも同一 seed なら同じ品揃え・価格・数量（stock_entry_id のみ異なる）。
	other, err := h.server.RegisterBuyer(context.Background(), &survivalv1.RegisterBuyerRequest{
		IdempotencyKey:   "buyer-" + store.NewUUID(),
		WorldId:          worldID,
		RegionId:         "region-1",
		Seed:             12345,
		InventoryTableId: testTableID,
		PriceModifierBp:  10000,
		SpawnAtUnixMs:    1_700_000_000_000,
		DespawnAtUnixMs:  1_700_000_600_000,
	})
	if err != nil {
		t.Fatalf("RegisterBuyer (別Buyer): %v", err)
	}
	if len(other.GetStock()) != len(first.GetStock()) {
		t.Fatalf("同一 seed で slot 数が違う: %d vs %d", len(other.GetStock()), len(first.GetStock()))
	}
	for i := range first.GetStock() {
		a, b := first.GetStock()[i], other.GetStock()[i]
		if a.GetItemDefinitionId() != b.GetItemDefinitionId() ||
			a.GetUnitPrice() != b.GetUnitPrice() ||
			a.GetRemainingQuantity() != b.GetRemainingQuantity() {
			t.Errorf("同一 seed で在庫が違う: slot %d: %+v vs %+v", i, a, b)
		}
		if a.GetStockEntryId() == b.GetStockEntryId() {
			t.Errorf("別 Buyer なのに stock_entry_id が同一: %s", a.GetStockEntryId())
		}
	}
}

// --- 売却 (3.7) -------------------------------------------------------------

func TestCommitSale(t *testing.T) {
	h := economySetup(t)
	worldID := h.newWorld(t)
	buyer := h.registerBuyer(t, worldID, 9)
	seller := store.NewUUID()

	// 売る物を用意する: stackable な cooked_meat を 1 個持たせる。
	invID, err := h.store.EnsureInventory(context.Background(), "character", seller, 24, 40_000)
	if err != nil {
		t.Fatalf("EnsureInventory: %v", err)
	}
	if err := h.store.UpsertEntry(context.Background(), invID, store.EntryRow{
		SlotIndex: 0, ItemDefinitionID: "cooked_meat", Quantity: 1,
	}); err != nil {
		t.Fatalf("UpsertEntry: %v", err)
	}
	sellPrice, ok := h.catalog.SellPrice("cooked_meat")
	if !ok || sellPrice <= 0 {
		t.Fatalf("cooked_meat の sell_price が未設定: %d", sellPrice)
	}

	key := "sale-" + store.NewUUID()
	req := &survivalv1.CommitSaleRequest{
		IdempotencyKey:  key,
		BuyerInstanceId: buyer.GetBuyerInstanceId(),
		SellerId:        seller,
		Items:           []*survivalv1.ItemRef{{ItemDefinitionId: "cooked_meat"}},
	}
	resp, err := h.server.CommitSale(context.Background(), req)
	if err != nil {
		t.Fatalf("CommitSale: %v", err)
	}
	if resp.GetStatus() != survivalv1.ResultStatus_RESULT_STATUS_OK {
		t.Fatalf("CommitSale: got %v want OK", resp.GetStatus())
	}
	// proceeds は Definition の sell_price（Client 入力ではない・MVP-SEC-006）。
	if got := resp.GetProceeds().GetAmount(); got != sellPrice {
		t.Errorf("proceeds = %d, want %d", got, sellPrice)
	}
	if got := h.balance(t, seller); got != sellPrice {
		t.Errorf("売却後の残高 = %d, want %d", got, sellPrice)
	}
	// インベントリから減っている。
	entries, err := h.store.GetEntries(context.Background(), invID)
	if err != nil {
		t.Fatal(err)
	}
	for _, e := range entries {
		if e.ItemDefinitionID == "cooked_meat" {
			t.Errorf("売ったのにインベントリに残っている: %+v", e)
		}
	}
	// version が上がっている。
	if resp.GetNewPersistedInventoryVersion() != 1 {
		t.Errorf("new_persisted_inventory_version = %d, want 1", resp.GetNewPersistedInventoryVersion())
	}

	// 冪等: 再送しても二重入金しない。
	dup, err := h.server.CommitSale(context.Background(), req)
	if err != nil {
		t.Fatalf("CommitSale 再送: %v", err)
	}
	if dup.GetStatus() != survivalv1.ResultStatus_RESULT_STATUS_DUPLICATE {
		t.Errorf("再送: got %v want DUPLICATE", dup.GetStatus())
	}
	if got := h.balance(t, seller); got != sellPrice {
		t.Errorf("再送で二重入金した: %d, want %d", got, sellPrice)
	}
}

// 所有していない物は売れない（在庫・台帳を動かさない）。
func TestCommitSaleRejectsUnownedItem(t *testing.T) {
	h := economySetup(t)
	worldID := h.newWorld(t)
	buyer := h.registerBuyer(t, worldID, 10)
	seller := store.NewUUID()

	resp, err := h.server.CommitSale(context.Background(), &survivalv1.CommitSaleRequest{
		IdempotencyKey:  "sale-" + store.NewUUID(),
		BuyerInstanceId: buyer.GetBuyerInstanceId(),
		SellerId:        seller,
		Items:           []*survivalv1.ItemRef{{ItemDefinitionId: "cooked_meat"}},
	})
	if err != nil {
		t.Fatalf("CommitSale: %v", err)
	}
	if resp.GetStatus() != survivalv1.ResultStatus_RESULT_STATUS_REJECTED {
		t.Fatalf("未所持の売却: got %v want REJECTED", resp.GetStatus())
	}
	if got := h.balance(t, seller); got != 0 {
		t.Errorf("未所持の売却で入金された: %d", got)
	}
}

// --- NATS 経済イベント (14.3) -----------------------------------------------

func TestEconomyEventsEnqueued(t *testing.T) {
	h := economySetup(t)
	worldID := h.newWorld(t)

	// 出現
	buyer := h.registerBuyer(t, worldID, 11)
	entry := buyer.GetStock()[0]
	purchaser := store.NewUUID()
	h.credit(t, purchaser, 1_000_000)

	// 購入
	if got := h.purchase(t, "k-"+store.NewUUID(), buyer.GetBuyerInstanceId(),
		entry.GetStockEntryId(), purchaser, 0); got.GetStatus() != survivalv1.PurchaseStatus_PURCHASE_STATUS_COMMITTED {
		t.Fatalf("purchase: got %v want COMMITTED", got.GetStatus())
	}
	// 消滅
	if _, err := h.server.DespawnBuyer(context.Background(), &survivalv1.DespawnBuyerRequest{
		BuyerInstanceId: buyer.GetBuyerInstanceId(),
		TargetStatus:    "DESPAWNED",
	}); err != nil {
		t.Fatalf("DespawnBuyer: %v", err)
	}

	events := h.economyEvents(t, worldID)
	byType := map[string]map[string]any{}
	for _, e := range events {
		typ, _ := e["type"].(string)
		byType[typ] = e
	}
	for _, want := range []string{"buyer_spawned", "purchase", "buyer_despawned"} {
		if _, ok := byType[want]; !ok {
			t.Errorf("world.%s.event.economy に %q が発行されていない（発行済: %v）", worldID, want, keysOf(byType))
		}
	}

	// 金額は JSON でも整数（小数化しない・3.8）。
	if p, ok := byType["purchase"]; ok {
		raw, err := json.Marshal(p["amount"])
		if err != nil {
			t.Fatal(err)
		}
		if s := string(raw); s != fmt.Sprintf("%d", entry.GetUnitPrice()) {
			t.Errorf("amount が整数で発行されていない: %s (want %d)", s, entry.GetUnitPrice())
		}
	}
}

func keysOf(m map[string]map[string]any) []string {
	out := make([]string, 0, len(m))
	for k := range m {
		out = append(out, k)
	}
	return out
}

// --- ランキング (12.3) ------------------------------------------------------

func TestRankingBatchComputesNetWorth(t *testing.T) {
	h := economySetup(t)
	owner := store.NewUUID()

	// 現金 + Item 評価額 が net_worth になること。
	h.credit(t, owner, 500)
	invID, err := h.store.EnsureInventory(context.Background(), "character", owner, 24, 40_000)
	if err != nil {
		t.Fatal(err)
	}
	if err := h.store.UpsertEntry(context.Background(), invID, store.EntryRow{
		SlotIndex: 0, ItemDefinitionID: "cooked_meat", Quantity: 3,
	}); err != nil {
		t.Fatal(err)
	}
	sellPrice, _ := h.catalog.SellPrice("cooked_meat")
	want := int64(500) + 3*sellPrice

	entries, err := h.store.ComputeNetWorth(context.Background())
	if err != nil {
		t.Fatalf("ComputeNetWorth: %v", err)
	}
	var got int64 = -1
	for _, e := range entries {
		if e.OwnerID == owner {
			got = e.NetWorth
			break
		}
	}
	if got != want {
		t.Errorf("net_worth = %d, want %d (現金500 + cooked_meat×3@%d)", got, want, sellPrice)
	}

	// price_version / calculated_at 付きで保存されること。
	version, err := h.store.NextPriceVersion(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if err := h.store.SaveRanking(context.Background(), version, entries); err != nil {
		t.Fatalf("SaveRanking: %v", err)
	}
	var savedWorth int64
	var savedAtIsSet bool
	if err := h.pool.QueryRow(context.Background(),
		`SELECT net_worth, calculated_at IS NOT NULL FROM asset_rankings
		  WHERE owner_id = $1 AND price_version = $2`,
		owner, version).Scan(&savedWorth, &savedAtIsSet); err != nil {
		t.Fatalf("read asset_rankings: %v", err)
	}
	if savedWorth != want {
		t.Errorf("保存された net_worth = %d, want %d", savedWorth, want)
	}
	if !savedAtIsSet {
		t.Error("calculated_at が保存されていない")
	}

	// 次の実行は price_version が単調増加する（世代管理）。
	next, err := h.store.NextPriceVersion(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if next <= version {
		t.Errorf("price_version が増えていない: %d → %d", version, next)
	}
}

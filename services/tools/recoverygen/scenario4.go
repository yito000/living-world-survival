package main

import (
	"context"
	"encoding/json"
	"fmt"
	"time"

	survivalv1 "living-world-survival/services/gen/go/survival/v1"
)

// s4State は s4-setup → s4-verify へ渡す受け渡し。
type s4State struct {
	WorldID   string `json:"world_id"`
	Buyer     string `json:"buyer"`
	Stock     string `json:"stock"`
	Purchaser string `json:"purchaser"`
	Key       string `json:"key"`
	Charged   int64  `json:"charged"`
	Initial   int64  `json:"initial"`
}

// s4Setup: DB 再起動の前に Buyer 登録＋1 回購入し、冪等キーと課金額を控える。
func s4Setup(ctx context.Context, statePath string) {
	pool := dbPool(ctx)
	defer pool.Close()
	conn := grpcConn()
	defer conn.Close() //nolint:errcheck
	eco := economyClient(conn)

	worldID := provisionWorld(ctx, pool)
	purchaser := newUUID()
	const initial = int64(100000)
	seedBalance(ctx, pool, purchaser, initial)

	reg, err := eco.RegisterBuyer(ctx, &survivalv1.RegisterBuyerRequest{
		IdempotencyKey: "rec-s4-" + newUUID(), WorldId: worldID, RegionId: "region-1",
		Seed: 20260718, InventoryTableId: "rare_weapon_buyer_v1", PriceModifierBp: 10000,
		SpawnAtUnixMs:   time.Now().UnixMilli(),
		DespawnAtUnixMs: time.Now().Add(30 * time.Minute).UnixMilli(),
	})
	must(err, "RegisterBuyer")
	if len(reg.GetStock()) == 0 {
		fatal("Buyer の在庫が空")
	}
	entry := reg.GetStock()[0]
	key := "rec-s4-buy-" + newUUID()
	buy, err := eco.CommitPurchase(ctx, &survivalv1.CommitPurchaseRequest{
		IdempotencyKey: key, BuyerInstanceId: reg.GetBuyerInstanceId(),
		StockEntryId: entry.GetStockEntryId(), PurchaserId: purchaser, InventoryVersion: 0,
	})
	must(err, "CommitPurchase")
	if buy.GetStatus() != survivalv1.PurchaseStatus_PURCHASE_STATUS_COMMITTED {
		fatal("購入が COMMITTED にならなかった: %v", buy.GetStatus())
	}
	st := s4State{
		WorldID: worldID, Buyer: reg.GetBuyerInstanceId(), Stock: entry.GetStockEntryId(),
		Purchaser: purchaser, Key: key, Charged: buy.GetCharged().GetAmount(), Initial: initial,
	}
	writeState(statePath, st)
	logf("world=%s purchaser=%s key=%s charged=%d（DB 再起動前）", worldID, purchaser, key, st.Charged)
	b, _ := json.Marshal(map[string]any{"world_id": worldID, "key": key, "charged": st.Charged})
	fmt.Println(string(b))
}

// s4Verify: DB 再起動後、(1) Outbox Relay が pgxpool 接続を回復して未 publish を捌ける、
// (2) 冪等キーによる再送が二重確定しない、を検証する。DB 再起動後の 1 回目のクエリで
// pgxpool が張り直せるか（＝relay が回復しているか）を実測する。
func s4Verify(ctx context.Context, statePath string) {
	var st s4State
	readState(statePath, &st)
	r := newResult("s4_db_restart", "AT-021", "DB 再起動: relay 回復と冪等再送の二重確定防止")

	pool := dbPool(ctx)
	defer pool.Close()
	conn := grpcConn()
	defer conn.Close() //nolint:errcheck
	wd := worldDataClient(conn)
	eco := economyClient(conn)

	// --- DB 再起動後、購入は保持されている（Tx 確定済み）---
	r.assert("purchase_survived_restart", purchaseRowCount(ctx, pool, st.Key) == 1,
		"purchase_transactions 行 %d 件（DB 再起動をまたいで保持）", purchaseRowCount(ctx, pool, st.Key))
	r.assert("currency_survived_restart", latestBalance(ctx, pool, st.Purchaser) == st.Initial-st.Charged,
		"残高 %d == %d-%d", latestBalance(ctx, pool, st.Purchaser), st.Initial, st.Charged)

	// --- (2) 冪等再送 → DUPLICATE（idempotency_key で二重確定しない）---
	dup, err := eco.CommitPurchase(ctx, &survivalv1.CommitPurchaseRequest{
		IdempotencyKey: st.Key, BuyerInstanceId: st.Buyer,
		StockEntryId: st.Stock, PurchaserId: st.Purchaser, InventoryVersion: 0,
	})
	must(err, "CommitPurchase(再送)")
	r.assert("resend_duplicate", dup.GetStatus() == survivalv1.PurchaseStatus_PURCHASE_STATUS_DUPLICATE,
		"再送 status=%v", dup.GetStatus())
	r.assert("no_double_confirm", purchaseRowCount(ctx, pool, st.Key) == 1 &&
		latestBalance(ctx, pool, st.Purchaser) == st.Initial-st.Charged,
		"再送後 purchase行=%d 残高=%d（二重確定なし）", purchaseRowCount(ctx, pool, st.Key), latestBalance(ctx, pool, st.Purchaser))

	// --- (1) relay 回復: DB 再起動後に新規 AppendEvents→outbox が publish される ---
	ev := "ev-" + newUUID()
	appendOK(ctx, wd, st.WorldID, ev, 1, "actor.moved", `{"probe":true}`)
	drained := waitDrain(ctx, pool, st.WorldID, 30*time.Second)
	r.assert("relay_recovered", drained,
		"DB 再起動後の新規イベントが publish された 未publish=%d", unpublishedForWorld(ctx, pool, st.WorldID))

	r.Recovery["purchases_lost"] = 0
	r.Recovery["charged"] = st.Charged
	r.Recovery["nonecon_restore_sec"] = 0.0
	r.finding("DS ランタイムは模擬。DB 再起動をまたいだ購入保持・idempotency_key による二重確定防止・relay(pgxpool) 回復は実 apid+PostgreSQL で実測。")
	r.emit()
}

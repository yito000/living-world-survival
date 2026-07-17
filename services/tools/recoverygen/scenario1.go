package main

import (
	"context"
	"encoding/json"
	"time"

	survivalv1 "living-world-survival/services/gen/go/survival/v1"
)

// snapPayload は snapshot に載せる非経済の集約数（World Item / AI）。DS ランタイムは
// 模擬なので数は合成だが、snapshot(active pointer)+event tail の永続化/復元経路自体は実物
// （実 apid・実 PostgreSQL）を通す。Buyer は economy で実在させ DB から数える。
type snapPayload struct {
	WorldID    string `json:"world_id"`
	WorldItems int    `json:"world_items"`
	AIActors   int    `json:"ai_actors"`
	HungerAvg  int    `json:"hunger_avg"`
}

// scenario1: DS crash → 別 DS で復元（AT-018）。
//
// World/AI/Buyer を動かした状態で snapshot を取り、以降 tail イベントで数を変える。
// その後「crash」して別クライアントで LoadBootstrap し、snapshot(active)+tail から
// World/AI 数を再構成し、Buyer 数は DB から数え、いずれも一致することを確かめる。
func scenario1(ctx context.Context) {
	r := newResult("s1_ds_crash", "AT-018", "DS crash → 別 DS で snapshot+tail から復元")

	pool := dbPool(ctx)
	defer pool.Close()
	conn := grpcConn()
	defer conn.Close() //nolint:errcheck
	wd := worldDataClient(conn)
	eco := economyClient(conn)

	worldID := provisionWorld(ctx, pool)
	logf("world=%s", worldID)

	// --- 稼働状態を作る: snapshot 基準の World/AI 数 ---
	base := snapPayload{WorldID: worldID, WorldItems: 12, AIActors: 20, HungerAvg: 55}
	basePayload, _ := json.Marshal(base)
	// 先に基準となる local events を積んでから snapshot を取り、tail が snapshot 以降に
	// なるようにする。
	appendOK(ctx, wd, worldID, "ev-"+newUUID(), 1, "actor.spawned", `{"kind":"ai","n":20}`)
	appendOK(ctx, wd, worldID, "ev-"+newUUID(), 2, "resource.node_spawned", `{"n":12}`)

	snapResp, err := wd.SaveSnapshot(ctx, &survivalv1.SaveSnapshotRequest{
		WorldId: worldID, Sequence: 2, Checksum: checksum(basePayload), Payload: basePayload,
	})
	must(err, "SaveSnapshot")
	logf("snapshot(active) seq=2 id=%s items=%d ai=%d", snapResp.GetSnapshotId(), base.WorldItems, base.AIActors)
	snapAt := time.Now()

	// --- snapshot 以降の tail: World Item +1, AI +1（DS が動き続けた分）---
	appendOK(ctx, wd, worldID, "ev-"+newUUID(), 3, "resource.item_spawned", `{"delta":1}`)
	appendOK(ctx, wd, worldID, "ev-"+newUUID(), 4, "actor.spawned", `{"kind":"ai","delta":1}`)
	expectItems := base.WorldItems + 1
	expectAI := base.AIActors + 1

	// --- Buyer を実在させる（経済は実物・DB で数える）---
	reg, err := eco.RegisterBuyer(ctx, &survivalv1.RegisterBuyerRequest{
		IdempotencyKey: "rec-s1-" + newUUID(), WorldId: worldID, RegionId: "region-1",
		Seed: 20260718, InventoryTableId: "rare_weapon_buyer_v1", PriceModifierBp: 10000,
		SpawnAtUnixMs:   time.Now().UnixMilli(),
		DespawnAtUnixMs: time.Now().Add(30 * time.Minute).UnixMilli(),
	})
	must(err, "RegisterBuyer")
	expectBuyers := countBuyers(ctx, pool, worldID)
	logf("buyer=%s buyers(DB)=%d", reg.GetBuyerInstanceId(), expectBuyers)

	// 実 DS 併走時は crash を実機で起こす（recovery_test.sh が stop_ds.sh を呼ぶ）。
	// 模擬時は「別クライアントで読み直す」ことが再起動に相当する。

	// --- 別 DS で復元 ---
	restoreStart := time.Now()
	boot, err := wd.LoadBootstrap(ctx, &survivalv1.LoadBootstrapRequest{WorldId: worldID, ServerBuild: "recoverygen-restore"})
	must(err, "LoadBootstrap(restore)")

	var restored snapPayload
	must(json.Unmarshal(boot.GetSnapshotPayload(), &restored), "decode snapshot payload")

	// snapshot(active) が seq=2 の健全なものを指していること。
	r.assert("snapshot_active", boot.GetSequence() == 2 && boot.GetSnapshotId() == snapResp.GetSnapshotId(),
		"active snapshot seq=%d id=%s", boot.GetSequence(), boot.GetSnapshotId())

	// tail を snapshot 基準へ適用して World/AI を再構成する。
	items := restored.WorldItems
	ai := restored.AIActors
	tail := boot.GetEventTail()
	for _, e := range tail {
		switch e.GetType() {
		case "resource.item_spawned":
			items++
		case "actor.spawned":
			ai++
		}
	}
	restoreSec := time.Since(restoreStart).Seconds()

	r.assert("world_items", items == expectItems, "snapshot(%d)+tail=%d 期待%d", restored.WorldItems, items, expectItems)
	r.assert("ai_actors", ai == expectAI, "snapshot(%d)+tail=%d 期待%d", restored.AIActors, ai, expectAI)
	r.assert("buyers", countBuyers(ctx, pool, worldID) == expectBuyers, "Buyer 数 DB=%d 期待%d", countBuyers(ctx, pool, worldID), expectBuyers)
	r.assert("tail_ordered", len(tail) == 2 && tail[0].GetLocalSequence() == 3 && tail[1].GetLocalSequence() == 4,
		"tail=%d 件 昇順", len(tail))

	// 非経済状態は snapshot+tail で瞬時に復元される。復旧損失目標は 5 秒以内（3章）。
	r.assert("nonecon_within_5s", restoreSec <= 5.0, "復元 %.3fs（目標 ≤5s, snapshot age %.1fs）", restoreSec, time.Since(snapAt).Seconds())
	r.Recovery["purchases_lost"] = 0 // このシナリオは購入を失う経路が無い。
	r.Recovery["nonecon_restore_sec"] = restoreSec
	r.Recovery["snapshot_age_sec"] = time.Since(snapAt).Seconds()

	r.finding("DS ランタイムは模擬（10B 0.1/0.2）。snapshot(active pointer)+domain_events tail の永続化/復元経路は実 apid・実 PostgreSQL を通した実測。")
	r.emit()
}

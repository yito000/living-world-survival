package main

import (
	"context"
	"encoding/json"

	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"

	survivalv1 "living-world-survival/services/gen/go/survival/v1"
)

// scenario5: Corrupt Snapshot（16章）。
//
// 検証は staging 段階で行われる（grpcserver.SaveSnapshot が store.SaveSnapshot を呼ぶ前に
// checksumMatches で弾く。落とし穴6章「checksum 検証を staging 段階で行う」）。したがって
// **本物のテスト可能な不変条件**は次:
//
//	payload/checksum 不一致の SaveSnapshot → InvalidArgument で拒否され、
//	active_snapshot_id は健全な直前 snapshot を指したまま、LoadBootstrap は直前 payload+tail を返す。
//
// あわせて DB の checksum 列を直接壊す変種も行うが、これは「load 時に再検証しない」ことを
// 明らかにするためであり、fallback を証明しない。実装が持たない挙動を PASS と偽らない。
func scenario5(ctx context.Context) {
	r := newResult("s5_corrupt_snapshot", "16章", "Corrupt Snapshot は staging→checksum で弾き active を汚さない")

	pool := dbPool(ctx)
	defer pool.Close()
	conn := grpcConn()
	defer conn.Close() //nolint:errcheck
	wd := worldDataClient(conn)

	worldID := provisionWorld(ctx, pool)
	logf("world=%s", worldID)

	// --- 健全な直前 snapshot（active）を作る ---
	good := snapPayload{WorldID: worldID, WorldItems: 7, AIActors: 15, HungerAvg: 60}
	goodPayload, _ := json.Marshal(good)
	appendOK(ctx, wd, worldID, "ev-"+newUUID(), 1, "actor.spawned", `{"n":15}`)
	goodSnap, err := wd.SaveSnapshot(ctx, &survivalv1.SaveSnapshotRequest{
		WorldId: worldID, Sequence: 1, Checksum: checksum(goodPayload), Payload: goodPayload,
	})
	must(err, "SaveSnapshot(good)")
	logf("健全 snapshot(active) seq=1 id=%s", goodSnap.GetSnapshotId())

	// snapshot 以降の tail（復元時に再適用される分）。
	appendOK(ctx, wd, worldID, "ev-"+newUUID(), 2, "resource.item_spawned", `{"delta":1}`)

	// --- Corrupt Snapshot を staging に投げる（payload と checksum が不一致）---
	corruptPayload := []byte(`{"world_id":"` + worldID + `","world_items":999,"ai_actors":999}`)
	wrongChecksum := checksum(goodPayload) // payload と一致しない checksum を渡す
	_, err = wd.SaveSnapshot(ctx, &survivalv1.SaveSnapshotRequest{
		WorldId: worldID, Sequence: 2, Checksum: wrongChecksum, Payload: corruptPayload,
	})
	st, _ := status.FromError(err)
	r.assert("staged_rejected", err != nil && st.Code() == codes.InvalidArgument,
		"SaveSnapshot(不一致) → %v", st.Code())

	// --- active pointer は健全な直前を指したまま ---
	var activeID string
	must(pool.QueryRow(ctx, `SELECT active_snapshot_id::text FROM worlds WHERE world_id=$1`, worldID).Scan(&activeID), "read active")
	r.assert("active_not_corrupted", activeID == goodSnap.GetSnapshotId(),
		"active_snapshot_id=%s（健全 seq=1 のまま）", activeID)

	boot, err := wd.LoadBootstrap(ctx, &survivalv1.LoadBootstrapRequest{WorldId: worldID, ServerBuild: "recoverygen-s5"})
	must(err, "LoadBootstrap")
	var restored snapPayload
	must(json.Unmarshal(boot.GetSnapshotPayload(), &restored), "decode payload")
	r.assert("loads_good_snapshot", boot.GetSequence() == 1 && restored.WorldItems == good.WorldItems,
		"LoadBootstrap seq=%d items=%d（健全値）", boot.GetSequence(), restored.WorldItems)
	r.assert("event_tail_after_good", len(boot.GetEventTail()) == 1 && boot.GetEventTail()[0].GetLocalSequence() == 2,
		"tail=%d 件（健全 snapshot 以降）", len(boot.GetEventTail()))

	// --- 変種: active snapshot の checksum 列を直接壊す（load 時再検証の有無を明らかにする）---
	_, err = pool.Exec(ctx,
		`UPDATE world_snapshots SET checksum='deadbeef' WHERE snapshot_id=$1`, goodSnap.GetSnapshotId())
	must(err, "corrupt checksum column")
	boot2, err := wd.LoadBootstrap(ctx, &survivalv1.LoadBootstrapRequest{WorldId: worldID, ServerBuild: "recoverygen-s5b"})
	if err == nil {
		var p snapPayload
		_ = json.Unmarshal(boot2.GetSnapshotPayload(), &p)
		// LoadWorldBootstrap は checksum を再検証しない（store/snapshot.go）。壊れた行でも
		// payload をそのまま返す。fallback は実装されていない。
		r.add("load_does_not_reverify", "NODATA",
			"active 行の checksum を壊しても LoadBootstrap は payload を返す（seq=%d）。load 時再検証・直前 fallback は未実装", boot2.GetSequence())
		r.finding("重要: active 行そのものが破損した場合の『直前 snapshot への自動 fallback』は未実装。checksum 検証は staging(SaveSnapshot) 段階のみで、LoadWorldBootstrap は再検証しない（store/snapshot.go）。指示書 16章の fallback は staging 拒否により active を汚さないことで担保される設計で、破損 active からの復旧経路は別途要件化が必要。")
	} else {
		r.add("load_does_not_reverify", "NODATA", "破損後 LoadBootstrap がエラー: %v", err)
	}

	r.Recovery["purchases_lost"] = 0
	r.Recovery["nonecon_restore_sec"] = 0.0
	r.emit()
}

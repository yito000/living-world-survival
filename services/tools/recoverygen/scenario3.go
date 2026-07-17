package main

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strings"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/nats-io/nats.go"
)

// s3State は s3-accumulate → s3-verify へ渡す受け渡し。
type s3State struct {
	WorldID  string `json:"world_id"`
	Appended int    `json:"appended"`
}

// s3Accumulate: NATS 停止中に DS が Gameplay 変更を続行する状況を作る（ゲームは止めない, 16章）。
// AppendEvents は DB(domain_events+outbox) を書くだけで NATS には同期発行しないため、NATS が
// 落ちていても成功し、outbox_messages に未 publish として滞留する。
func s3Accumulate(ctx context.Context, statePath string) {
	pool := dbPool(ctx)
	defer pool.Close()
	conn := grpcConn()
	defer conn.Close() //nolint:errcheck
	wd := worldDataClient(conn)

	worldID := provisionWorld(ctx, pool)
	const n = 8
	for i := 1; i <= n; i++ {
		appendOK(ctx, wd, worldID, "ev-"+newUUID(), int64(i), "actor.moved",
			fmt.Sprintf(`{"seq":%d,"x":%d}`, i, i))
	}
	pending := unpublishedForWorld(ctx, pool, worldID)
	logf("world=%s appended=%d outbox未publish=%d（NATS 停止中の滞留）", worldID, n, pending)

	writeState(statePath, s3State{WorldID: worldID, Appended: n})
	// 進捗 JSON（recovery_test.sh 用）。
	b, _ := json.Marshal(map[string]any{"world_id": worldID, "appended": n, "outbox_pending": pending})
	fmt.Println(string(b))
}

// s3Verify: NATS 復旧後に (1) outbox が順送 Flush されて未 publish が 0 になる（relay 回復）、
// (2) WorldState Consumer が inbox_dedup で event_id を一度だけ処理する、を検証する。
//
// (2) は「同一 event_id を JetStream に 2 回 publish → duplicate カウンタが +1」で直接証明する
// （落とし穴6章「重複排除は再起動後こそ本番」）。
func s3Verify(ctx context.Context, statePath string) {
	var st s3State
	readState(statePath, &st)
	r := newResult("s3_nats_restart", "16章", "NATS 再起動: outbox flush と inbox_dedup 一度だけ")

	pool := dbPool(ctx)
	defer pool.Close()

	// --- (1) relay が回復し未 publish が捌ける ---
	drained := waitDrain(ctx, pool, st.WorldID, 30*time.Second)
	remaining := unpublishedForWorld(ctx, pool, st.WorldID)
	r.assert("outbox_flushed", drained, "NATS 復旧後 未publish=%d（%d 件を Flush）", remaining, st.Appended)

	// --- (2) inbox_dedup: 同一 event_id を 2 回 publish → duplicate +1 ---
	dupOK, detail := dedupOnce(ctx)
	r.assert("inbox_dedup_once", dupOK, "%s", detail)

	r.Recovery["purchases_lost"] = 0 // このシナリオは購入を伴わない。
	r.Recovery["nonecon_restore_sec"] = 0.0
	r.finding("outbox 滞留→Flush は実 apid relay+実 PostgreSQL の実測。inbox_dedup は実 worldstate へ同一 event_id を 2 回 publish して duplicate カウンタで確認（JetStream 状態は無 volume で ephemeral のため、drain 済みイベントの二次処理には依存しない）。")
	r.emit()
}

// waitDrain は world の未 publish outbox が 0 になるまで待つ（relay の回復＝publish 再開）。
func waitDrain(ctx context.Context, pool *pgxpool.Pool, worldID string, timeout time.Duration) bool {
	deadline := time.Now().Add(timeout)
	for time.Now().Before(deadline) {
		if unpublishedForWorld(ctx, pool, worldID) == 0 {
			return true
		}
		time.Sleep(1 * time.Second)
	}
	return unpublishedForWorld(ctx, pool, worldID) == 0
}

// dedupOnce は同一 event_id の envelope を JetStream へ 2 回 publish し、worldstate の
// duplicate カウンタが少なくとも +1 されることを確認する。
func dedupOnce(ctx context.Context) (bool, string) {
	nc, err := nats.Connect(envOr("NATS_URL_HOST", "nats://localhost:4222"), nats.Timeout(5*time.Second))
	if err != nil {
		return false, fmt.Sprintf("NATS 接続失敗: %v", err)
	}
	defer nc.Close()
	js, err := nc.JetStream()
	if err != nil {
		return false, fmt.Sprintf("JetStream 取得失敗: %v", err)
	}

	worldID := newUUID()
	eventID := "dedup-" + newUUID()
	subject := "world." + worldID + ".event.actor"
	envelope := fmt.Sprintf(
		`{"event_id":%q,"world_id":%q,"aggregate_id":%q,"local_sequence":1,"sequence":1,"type":"actor.moved","payload":{"x":1},"occurred_at_unix_ms":%d}`,
		eventID, worldID, newUUID(), time.Now().UnixMilli())

	before := worldstateDuplicateTotal()
	// 2 回 publish（Nats-Msg-Id は付けない＝JetStream の重複排除ではなく inbox_dedup を試す）。
	for i := 0; i < 2; i++ {
		if _, err := js.Publish(subject, []byte(envelope)); err != nil {
			return false, fmt.Sprintf("publish 失敗(%d): %v", i, err)
		}
	}

	// worldstate が 2 通目を duplicate として弾くのを待つ。
	deadline := time.Now().Add(20 * time.Second)
	for time.Now().Before(deadline) {
		time.Sleep(1 * time.Second)
		after := worldstateDuplicateTotal()
		if after >= before+1 {
			return true, fmt.Sprintf("duplicate カウンタ %.0f→%.0f（同一 event_id を 1 度だけ処理）", before, after)
		}
	}
	after := worldstateDuplicateTotal()
	return false, fmt.Sprintf("duplicate 増分が観測できず（%.0f→%.0f）。worldstate の JetStream 再購読待ちの可能性", before, after)
}

// worldstateDuplicateTotal は worldstate /metrics の
// worldstate_events_processed_total{result="duplicate"} を読む。取れなければ 0。
func worldstateDuplicateTotal() float64 {
	url := envOr("WORLDSTATE_METRICS", "http://localhost:8083/metrics")
	resp, err := http.Get(url) //nolint:gosec,noctx // ローカル計測ツール
	if err != nil {
		return 0
	}
	defer resp.Body.Close() //nolint:errcheck
	body, err := io.ReadAll(resp.Body)
	if err != nil {
		return 0
	}
	for _, line := range strings.Split(string(body), "\n") {
		if strings.HasPrefix(line, "worldstate_events_processed_total{") && strings.Contains(line, `result="duplicate"`) {
			fields := strings.Fields(line)
			if len(fields) >= 2 {
				var v float64
				if _, err := fmt.Sscanf(fields[len(fields)-1], "%g", &v); err == nil {
					return v
				}
			}
		}
	}
	return 0
}

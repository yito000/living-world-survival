// Command m2smoke is a manual end-to-end smoke for the M2 persistence path
// (05B 5章). It plays the role of the Dedicated Server's RuntimePersistence
// Agent against a *running* apid: bootstrap → ready → inventory Domain Events
// via AppendEvents → outbox flush to NATS → SaveSnapshot → "DS restart" restore
// from snapshot + event tail. The DS runtime is the only simulated part; the
// API, PostgreSQL and NATS are real.
//
// Usage (from repo root, infra + api container up):
//
//	cd services/api && go run ./cmd/m2smoke
//
// Env overrides: DATABASE_URL_HOST, NATS_URL_HOST, API_GRPC_ADDR.
package main

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"reflect"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/nats-io/nats.go"
	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials/insecure"

	"living-world-survival/services/api/internal/store"
	survivalv1 "living-world-survival/services/gen/go/survival/v1"
)

func main() {
	ctx := context.Background()
	dbURL := envOr("DATABASE_URL_HOST", "postgres://survival:survival@localhost:5432/survival?sslmode=disable")
	natsURL := envOr("NATS_URL_HOST", "nats://localhost:4222")
	grpcAddr := envOr("API_GRPC_ADDR", "localhost:8092")

	pool, err := pgxpool.New(ctx, dbURL)
	must(err, "connect postgres")
	defer pool.Close()

	nc, err := nats.Connect(natsURL, nats.Timeout(3*time.Second))
	must(err, "connect nats")
	defer nc.Close()

	conn, err := grpc.NewClient(grpcAddr, grpc.WithTransportCredentials(insecure.NewCredentials()))
	must(err, "dial gRPC")
	defer func() { _ = conn.Close() }()
	wd := survivalv1.NewWorldDataServiceClient(conn)

	// Step 1: provision a world (real flow provisions on world/character init).
	worldID := store.NewUUID()
	_, err = pool.Exec(ctx, `INSERT INTO worlds (world_id) VALUES ($1)`, worldID)
	must(err, "provision world")
	step(1, "world provisioned: %s", worldID)

	// Subscribe to the world's event subjects before appending, so the outbox
	// relay's publish is observed (JetStream publish also reaches core subs).
	sub, err := nc.SubscribeSync("world." + worldID + ".>")
	must(err, "subscribe NATS")
	must(nc.Flush(), "flush NATS")

	// Step 2-3: DS bootstrap → ready. A fresh world returns an empty snapshot.
	boot, err := wd.LoadBootstrap(ctx, &survivalv1.LoadBootstrapRequest{WorldId: worldID, ServerBuild: "m2smoke"})
	must(err, "LoadBootstrap (bootstrap)")
	if boot.GetSnapshotId() != "" || boot.GetSequence() != 0 {
		fail("bootstrap should be empty for a new world: id=%q seq=%d", boot.GetSnapshotId(), boot.GetSequence())
	}
	step(2, "DS bootstrap complete → READY (snapshot='', sequence=0, payload=%s)", string(boot.GetSnapshotPayload()))

	// Step 4-5: inventory command → resulting Domain Events via AppendEvents.
	// (The DS applies ADD/MOVE/DROP to runtime state and emits these events.)
	addID := "ev-" + store.NewUUID()
	appendOK(ctx, wd, worldID, addID, 1, "inventory.item_added", `{"cmd":"ADD","slot":0,"item_definition_id":"stone","quantity":5}`)
	step(4, "ADD command event persisted (event_id=%s)", addID)

	// Idempotency (AT-003): re-sending the same event_id is a no-op DUPLICATE.
	dupResp, err := wd.AppendEvents(ctx, &survivalv1.AppendEventsRequest{ServerId: "m2smoke", Events: []*survivalv1.DomainEvent{
		mkEvent(worldID, addID, 1, "inventory.item_added", `{"cmd":"ADD","slot":0,"item_definition_id":"stone","quantity":5}`),
	}})
	must(err, "AppendEvents (resend)")
	if dupResp.GetResults()[0] != survivalv1.ResultStatus_RESULT_STATUS_DUPLICATE {
		fail("resend should be DUPLICATE, got %v", dupResp.GetResults()[0])
	}
	step(5, "idempotency verified (resend → DUPLICATE, AT-003)")

	// A second ADD to build history before the snapshot.
	add2ID := "ev-" + store.NewUUID()
	appendOK(ctx, wd, worldID, add2ID, 2, "inventory.item_added", `{"cmd":"ADD","slot":1,"item_definition_id":"wood","quantity":3}`)

	// Step 6: outbox flush → event durably on WorldData + delivered to NATS.
	assertPersistedSeq(ctx, pool, addID, 1)
	assertPersistedSeq(ctx, pool, add2ID, 2)
	waitNATS(sub, 2)
	assertOutboxPublished(ctx, pool, worldID)
	step(6, "outbox flushed: domain_events persisted (seq 1,2) + delivered to NATS + published_at set")

	// Snapshot at the current sequence (DS 30s snapshot analog).
	snapPayload := []byte(fmt.Sprintf(`{"world_id":%q,"inventory":{"0":{"item":"stone","qty":5},"1":{"item":"wood","qty":3}}}`, worldID))
	snapResp, err := wd.SaveSnapshot(ctx, &survivalv1.SaveSnapshotRequest{
		WorldId: worldID, Sequence: 2, Checksum: checksum(snapPayload), Payload: snapPayload,
	})
	must(err, "SaveSnapshot")
	step(6, "snapshot saved at sequence=2 (snapshot_id=%s)", snapResp.GetSnapshotId())

	// More events AFTER the snapshot (these form the restore tail).
	moveID := "ev-" + store.NewUUID()
	appendOK(ctx, wd, worldID, moveID, 3, "inventory.item_moved", `{"cmd":"MOVE","from":0,"to":2}`)
	dropID := "ev-" + store.NewUUID()
	appendOK(ctx, wd, worldID, dropID, 4, "inventory.item_dropped", `{"cmd":"DROP","slot":1,"quantity":1}`)

	// Step 7: DS restart → restore source = snapshot + event tail (sequence > snapshot).
	restore, err := wd.LoadBootstrap(ctx, &survivalv1.LoadBootstrapRequest{WorldId: worldID, ServerBuild: "m2smoke"})
	must(err, "LoadBootstrap (restore)")
	if restore.GetSnapshotId() != snapResp.GetSnapshotId() || restore.GetSequence() != 2 {
		fail("restore snapshot mismatch: id=%q seq=%d", restore.GetSnapshotId(), restore.GetSequence())
	}
	if !jsonEqual(restore.GetSnapshotPayload(), snapPayload) {
		fail("restore payload content mismatch: %s", string(restore.GetSnapshotPayload()))
	}
	tail := restore.GetEventTail()
	if len(tail) != 2 || tail[0].GetLocalSequence() != 3 || tail[1].GetLocalSequence() != 4 {
		fail("restore tail should be [seq3, seq4] ascending, got %d events", len(tail))
	}
	step(7, "DS restart restore OK: snapshot(seq=2) + event tail [MOVE seq3, DROP seq4] in ascending order")

	fmt.Println("\n✅ M2 E2E SMOKE PASSED (persistence path verified end-to-end through the running apid)")
}

// --- helpers ---------------------------------------------------------------

func mkEvent(worldID, eventID string, local int64, typ, payload string) *survivalv1.DomainEvent {
	return &survivalv1.DomainEvent{
		EventId:          eventID,
		WorldId:          worldID,
		AggregateId:      store.NewUUID(),
		LocalSequence:    local,
		Type:             typ,
		Payload:          []byte(payload),
		OccurredAtUnixMs: time.Now().UnixMilli(),
	}
}

func appendOK(ctx context.Context, wd survivalv1.WorldDataServiceClient, worldID, eventID string, local int64, typ, payload string) {
	resp, err := wd.AppendEvents(ctx, &survivalv1.AppendEventsRequest{
		ServerId: "m2smoke",
		Events:   []*survivalv1.DomainEvent{mkEvent(worldID, eventID, local, typ, payload)},
	})
	must(err, "AppendEvents "+typ)
	if resp.GetResults()[0] != survivalv1.ResultStatus_RESULT_STATUS_OK {
		fail("AppendEvents %s: got %v want OK", typ, resp.GetResults()[0])
	}
}

func assertPersistedSeq(ctx context.Context, pool *pgxpool.Pool, eventID string, wantSeq int64) {
	var seq int64
	err := pool.QueryRow(ctx, `SELECT sequence FROM domain_events WHERE event_id=$1`, eventID).Scan(&seq)
	must(err, "query sequence")
	if seq != wantSeq {
		fail("event %s: sequence=%d want %d", eventID, seq, wantSeq)
	}
}

func assertOutboxPublished(ctx context.Context, pool *pgxpool.Pool, worldID string) {
	// Give the running relay (≤1s tick) a moment, then confirm no unpublished
	// rows remain for this world.
	deadline := time.Now().Add(4 * time.Second)
	for {
		var unpublished int
		err := pool.QueryRow(ctx,
			`SELECT count(*) FROM outbox_messages WHERE topic LIKE 'world.'||$1||'.event.%' AND published_at IS NULL`,
			worldID).Scan(&unpublished)
		must(err, "query outbox")
		if unpublished == 0 {
			return
		}
		if time.Now().After(deadline) {
			fail("outbox still has %d unpublished rows after relay window", unpublished)
		}
		time.Sleep(300 * time.Millisecond)
	}
}

func waitNATS(sub *nats.Subscription, atLeast int) {
	got := 0
	for got < atLeast {
		msg, err := sub.NextMsg(5 * time.Second)
		if err != nil {
			fail("NATS receive: expected %d messages, got %d (%v)", atLeast, got, err)
		}
		fmt.Printf("      · NATS ← %s  %s\n", msg.Subject, string(msg.Data))
		got++
	}
}

func checksum(payload []byte) string {
	s := sha256.Sum256(payload)
	return hex.EncodeToString(s[:])
}

func jsonEqual(a, b []byte) bool {
	var av, bv any
	if json.Unmarshal(a, &av) != nil || json.Unmarshal(b, &bv) != nil {
		return false
	}
	return reflect.DeepEqual(av, bv)
}

func step(n int, format string, args ...any) {
	fmt.Printf("  [step %d] %s\n", n, fmt.Sprintf(format, args...))
}

func must(err error, what string) {
	if err != nil {
		fail("%s: %v", what, err)
	}
}

func fail(format string, args ...any) {
	fmt.Printf("\n❌ M2 E2E SMOKE FAILED: %s\n", fmt.Sprintf(format, args...))
	os.Exit(1)
}

func envOr(key, fallback string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return fallback
}

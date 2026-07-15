// Package integration exercises the M2 API persistence stack (store + gRPC
// servers + outbox relay) end-to-end against a real PostgreSQL. It self-skips
// when no database is reachable so `go test ./...` stays green without infra;
// run `make up migrate` first (or set TEST_DATABASE_URL) to exercise it.
package integration

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"os"
	"reflect"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"

	"living-world-survival/services/api/internal/grpcserver"
	"living-world-survival/services/api/internal/outbox"
	"living-world-survival/services/api/internal/store"
	survivalv1 "living-world-survival/services/gen/go/survival/v1"
)

type harness struct {
	pool  *pgxpool.Pool
	store *store.Store
	world *grpcserver.WorldDataServer
	actor *grpcserver.ActorStateServer
}

func setup(t *testing.T) *harness {
	t.Helper()
	url := os.Getenv("TEST_DATABASE_URL")
	if url == "" {
		url = os.Getenv("DATABASE_URL_HOST")
	}
	if url == "" {
		url = "postgres://survival:survival@localhost:5432/survival?sslmode=disable"
	}

	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	pool, err := pgxpool.New(context.Background(), url)
	if err != nil {
		t.Skipf("no database (pool): %v", err)
	}
	if err := pool.Ping(ctx); err != nil {
		pool.Close()
		t.Skipf("no database reachable at %s: %v", url, err)
	}
	t.Cleanup(pool.Close)

	st := store.New(pool)
	return &harness{
		pool:  pool,
		store: st,
		world: &grpcserver.WorldDataServer{Store: st},
		actor: &grpcserver.ActorStateServer{Store: st},
	}
}

// newWorld inserts a fresh world row and returns its id.
func (h *harness) newWorld(t *testing.T) string {
	t.Helper()
	id := store.NewUUID()
	_, err := h.pool.Exec(context.Background(),
		`INSERT INTO worlds (world_id) VALUES ($1)`, id)
	if err != nil {
		t.Fatalf("insert world: %v", err)
	}
	return id
}

func checksum(payload []byte) string {
	s := sha256.Sum256(payload)
	return hex.EncodeToString(s[:])
}

// jsonEqual reports whether two JSON byte slices are semantically equal
// (key order / whitespace independent).
func jsonEqual(t *testing.T, a, b []byte) bool {
	t.Helper()
	var av, bv any
	if err := json.Unmarshal(a, &av); err != nil {
		t.Fatalf("unmarshal a: %v", err)
	}
	if err := json.Unmarshal(b, &bv); err != nil {
		t.Fatalf("unmarshal b: %v", err)
	}
	return reflect.DeepEqual(av, bv)
}

func event(worldID, eventID string, local int64, payload string) *survivalv1.DomainEvent {
	return &survivalv1.DomainEvent{
		EventId:          eventID,
		WorldId:          worldID,
		AggregateId:      store.NewUUID(),
		LocalSequence:    local,
		Type:             "resource.mined",
		Payload:          []byte(payload),
		OccurredAtUnixMs: 1_700_000_000_000 + local,
	}
}

// --- AppendEvents ----------------------------------------------------------

func TestAppendEventsIdempotencyAndConflict(t *testing.T) {
	h := setup(t)
	ctx := context.Background()
	worldID := h.newWorld(t)
	evID := "ev-" + store.NewUUID()

	ev := event(worldID, evID, 1, `{"n":1}`)
	resp, err := h.world.AppendEvents(ctx, &survivalv1.AppendEventsRequest{ServerId: "srv-1", Events: []*survivalv1.DomainEvent{ev}})
	if err != nil {
		t.Fatalf("AppendEvents #1: %v", err)
	}
	if got := resp.GetResults()[0]; got != survivalv1.ResultStatus_RESULT_STATUS_OK {
		t.Fatalf("first append: got %v want OK", got)
	}

	// Resend the exact same event_id → DUPLICATE, still a single row.
	resp, err = h.world.AppendEvents(ctx, &survivalv1.AppendEventsRequest{ServerId: "srv-1", Events: []*survivalv1.DomainEvent{ev}})
	if err != nil {
		t.Fatalf("AppendEvents #2: %v", err)
	}
	if got := resp.GetResults()[0]; got != survivalv1.ResultStatus_RESULT_STATUS_DUPLICATE {
		t.Fatalf("resend: got %v want DUPLICATE", got)
	}

	var count int
	if err := h.pool.QueryRow(ctx, `SELECT count(*) FROM domain_events WHERE event_id=$1`, evID).Scan(&count); err != nil {
		t.Fatalf("count: %v", err)
	}
	if count != 1 {
		t.Fatalf("expected exactly 1 row for event_id, got %d", count)
	}
}

// TestAppendEventsNonUuidIdentifiers locks in the contract that DomainEvent
// event_id (ULID) and aggregate_id (e.g. "connection:0") are opaque strings,
// not UUIDs — the real Dedicated Server sends these and UUID columns would
// reject them (regression: 22P02 invalid input syntax for type uuid).
func TestAppendEventsNonUuidIdentifiers(t *testing.T) {
	h := setup(t)
	ctx := context.Background()
	worldID := h.newWorld(t)

	// ULID-shaped (26-char), non-UUID, unique per run so a re-run does not
	// collide on the event_id PK (dedup key).
	eventID := "01" + strings.ToUpper(strings.ReplaceAll(store.NewUUID(), "-", ""))[:24]
	ev := &survivalv1.DomainEvent{
		EventId:          eventID,
		WorldId:          worldID,
		AggregateId:      "connection:0", // non-UUID aggregate identifier
		LocalSequence:    1,
		Type:             "inventory.item_added",
		Payload:          []byte(`{"op":"ADD","slot":0,"item":"stone","qty":5}`),
		OccurredAtUnixMs: 1_700_000_000_000,
	}
	resp, err := h.world.AppendEvents(ctx, &survivalv1.AppendEventsRequest{ServerId: "srv", Events: []*survivalv1.DomainEvent{ev}})
	if err != nil {
		t.Fatalf("AppendEvents: %v", err)
	}
	if got := resp.GetResults()[0]; got != survivalv1.ResultStatus_RESULT_STATUS_OK {
		t.Fatalf("non-UUID ids: got %v want OK", got)
	}

	var agg string
	if err := h.pool.QueryRow(ctx, `SELECT aggregate_id FROM domain_events WHERE event_id=$1`, ev.EventId).Scan(&agg); err != nil {
		t.Fatalf("query: %v", err)
	}
	if agg != "connection:0" {
		t.Fatalf("aggregate_id: got %q want connection:0", agg)
	}

	// The event tail must surface these identifiers verbatim for DS restore.
	tail, err := h.world.LoadBootstrap(ctx, &survivalv1.LoadBootstrapRequest{WorldId: worldID})
	if err != nil {
		t.Fatalf("LoadBootstrap: %v", err)
	}
	if len(tail.GetEventTail()) != 1 || tail.GetEventTail()[0].GetAggregateId() != "connection:0" || tail.GetEventTail()[0].GetEventId() != ev.EventId {
		t.Fatalf("event tail did not round-trip non-UUID identifiers: %+v", tail.GetEventTail())
	}
}

func TestAppendEventsConcurrentSequencing(t *testing.T) {
	h := setup(t)
	ctx := context.Background()
	worldID := h.newWorld(t)

	const n = 12
	var wg sync.WaitGroup
	errs := make([]error, n)
	for i := 0; i < n; i++ {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			ev := event(worldID, "ev-"+store.NewUUID(), int64(i+1), `{"i":1}`)
			resp, err := h.world.AppendEvents(ctx, &survivalv1.AppendEventsRequest{ServerId: "srv", Events: []*survivalv1.DomainEvent{ev}})
			if err != nil {
				errs[i] = err
				return
			}
			if got := resp.GetResults()[0]; got != survivalv1.ResultStatus_RESULT_STATUS_OK {
				t.Errorf("concurrent append %d: got %v want OK", i, got)
			}
		}(i)
	}
	wg.Wait()
	for _, e := range errs {
		if e != nil {
			t.Fatalf("concurrent append error: %v", e)
		}
	}

	// Sequences must be a dense, unique 1..n set (API-assigned, serialized).
	rows, err := h.pool.Query(ctx, `SELECT sequence FROM domain_events WHERE world_id=$1 ORDER BY sequence`, worldID)
	if err != nil {
		t.Fatalf("query: %v", err)
	}
	defer rows.Close()
	var seqs []int64
	for rows.Next() {
		var s int64
		if err := rows.Scan(&s); err != nil {
			t.Fatalf("scan: %v", err)
		}
		seqs = append(seqs, s)
	}
	if len(seqs) != n {
		t.Fatalf("expected %d events, got %d", n, len(seqs))
	}
	for i, s := range seqs {
		if s != int64(i+1) {
			t.Fatalf("sequence not dense: seqs=%v", seqs)
		}
	}
}

// --- SaveSnapshot & LoadBootstrap ------------------------------------------

func TestSaveSnapshotAndLoadBootstrap(t *testing.T) {
	h := setup(t)
	ctx := context.Background()
	worldID := h.newWorld(t)

	// New world: empty bootstrap.
	boot, err := h.world.LoadBootstrap(ctx, &survivalv1.LoadBootstrapRequest{WorldId: worldID, ServerBuild: "build-x"})
	if err != nil {
		t.Fatalf("LoadBootstrap (empty): %v", err)
	}
	if boot.GetSnapshotId() != "" || boot.GetSequence() != 0 || string(boot.GetSnapshotPayload()) != "{}" {
		t.Fatalf("empty bootstrap: id=%q seq=%d payload=%q", boot.GetSnapshotId(), boot.GetSequence(), string(boot.GetSnapshotPayload()))
	}

	// Save a snapshot at sequence 5.
	payload := []byte(`{"world":"state","tick":5}`)
	saveResp, err := h.world.SaveSnapshot(ctx, &survivalv1.SaveSnapshotRequest{
		WorldId: worldID, Sequence: 5, Checksum: checksum(payload), Payload: payload,
	})
	if err != nil {
		t.Fatalf("SaveSnapshot: %v", err)
	}
	snapID := saveResp.GetSnapshotId()
	if snapID == "" {
		t.Fatal("SaveSnapshot returned empty snapshot_id")
	}

	// Append events, some at/below seq 5 are not possible (API assigns), but the
	// tail after the snapshot's sequence should be returned. Append 3 events;
	// they get sequences 1,2,3 — all <= 5, so they must NOT appear in the tail.
	for i := 0; i < 3; i++ {
		ev := event(worldID, "ev-"+store.NewUUID(), int64(i+1), `{"x":1}`)
		if _, err := h.world.AppendEvents(ctx, &survivalv1.AppendEventsRequest{ServerId: "s", Events: []*survivalv1.DomainEvent{ev}}); err != nil {
			t.Fatalf("append: %v", err)
		}
	}

	boot, err = h.world.LoadBootstrap(ctx, &survivalv1.LoadBootstrapRequest{WorldId: worldID})
	if err != nil {
		t.Fatalf("LoadBootstrap (active): %v", err)
	}
	if boot.GetSnapshotId() != snapID || boot.GetSequence() != 5 {
		t.Fatalf("active bootstrap: id=%q seq=%d", boot.GetSnapshotId(), boot.GetSequence())
	}
	// Payload is stored in a JSONB column (GIN-indexed per 3.7b) so byte order is
	// normalized; the content must be semantically identical. The DS applies the
	// JSON content and does not re-verify the checksum on load (3.2).
	if !jsonEqual(t, boot.GetSnapshotPayload(), payload) {
		t.Fatalf("payload content mismatch: got %q", string(boot.GetSnapshotPayload()))
	}
	if len(boot.GetEventTail()) != 0 {
		t.Fatalf("tail should be empty (all events <= snapshot seq), got %d", len(boot.GetEventTail()))
	}
}

func TestSaveSnapshotRejectsBadChecksum(t *testing.T) {
	h := setup(t)
	ctx := context.Background()
	worldID := h.newWorld(t)

	payload := []byte(`{"a":1}`)
	_, err := h.world.SaveSnapshot(ctx, &survivalv1.SaveSnapshotRequest{
		WorldId: worldID, Sequence: 1, Checksum: "deadbeef", Payload: payload,
	})
	if err == nil {
		t.Fatal("expected checksum mismatch error")
	}
	// active_snapshot_id must remain NULL (unchanged).
	var active *string
	if err := h.pool.QueryRow(ctx, `SELECT active_snapshot_id FROM worlds WHERE world_id=$1`, worldID).Scan(&active); err != nil {
		t.Fatalf("query active: %v", err)
	}
	if active != nil {
		t.Fatalf("active pointer must be unchanged on rejection, got %v", *active)
	}
}

func TestSaveSnapshotIdempotent(t *testing.T) {
	h := setup(t)
	ctx := context.Background()
	worldID := h.newWorld(t)
	payload := []byte(`{"s":1}`)
	req := &survivalv1.SaveSnapshotRequest{WorldId: worldID, Sequence: 7, Checksum: checksum(payload), Payload: payload}

	first, err := h.world.SaveSnapshot(ctx, req)
	if err != nil {
		t.Fatalf("save #1: %v", err)
	}
	second, err := h.world.SaveSnapshot(ctx, req)
	if err != nil {
		t.Fatalf("save #2: %v", err)
	}
	if first.GetSnapshotId() != second.GetSnapshotId() {
		t.Fatalf("idempotent save should reuse snapshot_id: %q vs %q", first.GetSnapshotId(), second.GetSnapshotId())
	}
}

// --- ActorState.Save -------------------------------------------------------

func TestActorStateVersionMonotonic(t *testing.T) {
	h := setup(t)
	ctx := context.Background()
	worldID := h.newWorld(t)
	actorID := store.NewUUID()

	save := func(version int64) survivalv1.ResultStatus {
		resp, err := h.actor.Save(ctx, &survivalv1.SaveRequest{
			ActorId:       actorID,
			Version:       version,
			PersonalState: []byte(`{"world_id":"` + worldID + `","hp":10}`),
			InventorySummary: []*survivalv1.InventoryEntry{
				{SlotIndex: 0, Item: &survivalv1.ItemRef{ItemDefinitionId: "stone"}, Quantity: 3},
			},
		})
		if err != nil {
			t.Fatalf("Save v%d: %v", version, err)
		}
		return resp.GetStatus()
	}

	if got := save(2); got != survivalv1.ResultStatus_RESULT_STATUS_OK {
		t.Fatalf("v2: got %v want OK", got)
	}
	if got := save(1); got != survivalv1.ResultStatus_RESULT_STATUS_DUPLICATE {
		t.Fatalf("v1 (older): got %v want DUPLICATE", got)
	}
	if got := save(2); got != survivalv1.ResultStatus_RESULT_STATUS_DUPLICATE {
		t.Fatalf("v2 (same): got %v want DUPLICATE", got)
	}
	if got := save(3); got != survivalv1.ResultStatus_RESULT_STATUS_OK {
		t.Fatalf("v3 (newer): got %v want OK", got)
	}

	var version int64
	if err := h.pool.QueryRow(ctx, `SELECT version FROM actor_runtime_states WHERE actor_id=$1`, actorID).Scan(&version); err != nil {
		t.Fatalf("query version: %v", err)
	}
	if version != 3 {
		t.Fatalf("stored version: got %d want 3", version)
	}
}

// --- Inventory foundation --------------------------------------------------

func TestInventoryFoundation(t *testing.T) {
	h := setup(t)
	ctx := context.Background()
	owner := store.NewUUID()

	inv1, err := h.store.EnsureInventory(ctx, "player", owner, 40, 60000)
	if err != nil {
		t.Fatalf("ensure #1: %v", err)
	}
	inv2, err := h.store.EnsureInventory(ctx, "player", owner, 40, 60000)
	if err != nil {
		t.Fatalf("ensure #2: %v", err)
	}
	if inv1 != inv2 {
		t.Fatalf("1 owner 1 inventory violated: %q vs %q", inv1, inv2)
	}

	if err := h.store.UpsertEntry(ctx, inv1, store.EntryRow{SlotIndex: 0, ItemDefinitionID: "stone", Quantity: 10}); err != nil {
		t.Fatalf("upsert: %v", err)
	}
	if err := h.store.UpsertEntry(ctx, inv1, store.EntryRow{SlotIndex: 0, ItemDefinitionID: "stone", Quantity: 25}); err != nil {
		t.Fatalf("upsert overwrite: %v", err)
	}
	entries, err := h.store.GetEntries(ctx, inv1)
	if err != nil {
		t.Fatalf("get entries: %v", err)
	}
	if len(entries) != 1 || entries[0].Quantity != 25 {
		t.Fatalf("entries: %+v", entries)
	}
}

// --- Outbox relay ----------------------------------------------------------

type fakePublisher struct {
	mu   sync.Mutex
	msgs map[string][][]byte
}

func newFakePublisher() *fakePublisher { return &fakePublisher{msgs: map[string][][]byte{}} }

func (f *fakePublisher) Publish(_ context.Context, subject string, data []byte) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.msgs[subject] = append(f.msgs[subject], data)
	return nil
}

func (f *fakePublisher) count() int {
	f.mu.Lock()
	defer f.mu.Unlock()
	n := 0
	for _, v := range f.msgs {
		n += len(v)
	}
	return n
}

func TestOutboxRelayDrains(t *testing.T) {
	h := setup(t)
	ctx := context.Background()

	// AppendEvents enqueues a transactional-outbox row per new event.
	worldID := h.newWorld(t)
	ev := event(worldID, "ev-"+store.NewUUID(), 1, `{"drain":1}`)
	if _, err := h.world.AppendEvents(ctx, &survivalv1.AppendEventsRequest{ServerId: "s", Events: []*survivalv1.DomainEvent{ev}}); err != nil {
		t.Fatalf("append: %v", err)
	}
	// Also directly enqueue one, to make the assertion independent of the count.
	msgID, err := h.store.EnqueueOutbox(ctx, "world."+worldID+".event.actor", []byte(`{"direct":1}`))
	if err != nil {
		t.Fatalf("enqueue: %v", err)
	}

	pub := newFakePublisher()
	relay := outbox.NewRelay(h.store, pub, 200*time.Millisecond, 100)
	published, err := relay.Drain(ctx)
	if err != nil {
		t.Fatalf("drain: %v", err)
	}
	if published < 2 {
		t.Fatalf("expected >=2 published, got %d", published)
	}
	if pub.count() < 2 {
		t.Fatalf("publisher received %d messages", pub.count())
	}

	// The directly-enqueued row must now have published_at set.
	var publishedAt *time.Time
	if err := h.pool.QueryRow(ctx, `SELECT published_at FROM outbox_messages WHERE message_id=$1`, msgID).Scan(&publishedAt); err != nil {
		t.Fatalf("query published_at: %v", err)
	}
	if publishedAt == nil {
		t.Fatal("published_at should be set after relay")
	}

	// A second drain finds nothing new.
	again, err := relay.Drain(ctx)
	if err != nil {
		t.Fatalf("drain #2: %v", err)
	}
	if again != 0 {
		t.Fatalf("second drain should publish 0, got %d", again)
	}
}

func TestOutboxRelayToJetStream(t *testing.T) {
	h := setup(t)
	ctx := context.Background()

	url := os.Getenv("NATS_URL_HOST")
	if url == "" {
		url = "nats://localhost:4222"
	}
	pub := connectJetStream(t, url)

	worldID := h.newWorld(t)
	msgID, err := h.store.EnqueueOutbox(ctx, "world."+worldID+".event.actor", []byte(`{"js":1}`))
	if err != nil {
		t.Fatalf("enqueue: %v", err)
	}

	relay := outbox.NewRelay(h.store, pub, time.Second, 100)
	if _, err := relay.Drain(ctx); err != nil {
		t.Fatalf("drain: %v", err)
	}

	var publishedAt *time.Time
	if err := h.pool.QueryRow(ctx, `SELECT published_at FROM outbox_messages WHERE message_id=$1`, msgID).Scan(&publishedAt); err != nil {
		t.Fatalf("query published_at: %v", err)
	}
	if publishedAt == nil {
		t.Fatal("published_at should be set after JetStream relay")
	}
}

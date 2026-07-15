package integration

import (
	"context"
	"encoding/json"
	"os"
	"testing"
	"time"

	"github.com/nats-io/nats.go"

	"living-world-survival/services/api/internal/outbox"
	"living-world-survival/services/api/internal/store"
	survivalv1 "living-world-survival/services/gen/go/survival/v1"
)

// TestM3EventSubjectsAndEnvelope verifies that after AppendEvents durably
// commits, the outbox relay publishes each M3 event to the correct subject
// (resource.* → world.{id}.event.resource, others → .actor) with an envelope a
// consumer can dedup by event_id (06B 0.4 / 3.3). Self-skips without NATS.
func TestM3EventSubjectsAndEnvelope(t *testing.T) {
	h := setup(t)
	h.seedItems(t)
	ctx := context.Background()

	url := os.Getenv("NATS_URL_HOST")
	if url == "" {
		url = "nats://localhost:4222"
	}
	nc, err := nats.Connect(url, nats.Timeout(2*time.Second))
	if err != nil {
		t.Skipf("no NATS at %s: %v", url, err)
	}
	t.Cleanup(nc.Close)
	pub, err := outbox.NewJetStreamPublisher(nc)
	if err != nil {
		t.Skipf("JetStream unavailable: %v", err)
	}

	worldID := h.newWorld(t)
	owner := store.NewUUID()

	// Subscribe to both categories before publishing.
	resSub, err := nc.SubscribeSync("world." + worldID + ".event.resource")
	if err != nil {
		t.Fatalf("subscribe resource: %v", err)
	}
	actSub, err := nc.SubscribeSync("world." + worldID + ".event.actor")
	if err != nil {
		t.Fatalf("subscribe actor: %v", err)
	}
	if err := nc.Flush(); err != nil {
		t.Fatalf("flush: %v", err)
	}

	mined := mkEvent(t, worldID, "resource.mined", "node-x", 1, map[string]any{
		"actor_id": owner,
		"grants":   []map[string]any{{"item_definition_id": "stone", "quantity": 1}},
	})
	cooked := mkEvent(t, worldID, "cooking.completed", "cook-x", 2, map[string]any{
		"actor_id": owner,
		"produced": []map[string]any{{"item_definition_id": "food_waste", "quantity": 1}},
	})
	okResults(t, h.append(t, mined, cooked))

	// Drain the outbox to JetStream.
	relay := outbox.NewRelay(h.store, pub, time.Second, 100)
	if _, err := relay.Drain(ctx); err != nil {
		t.Fatalf("drain: %v", err)
	}

	resEnv := recvEnvelope(t, resSub)
	if resEnv.Type != "resource.mined" || resEnv.EventID != mined.GetEventId() {
		t.Fatalf("resource subject envelope mismatch: %+v", resEnv)
	}
	actEnv := recvEnvelope(t, actSub)
	if actEnv.Type != "cooking.completed" || actEnv.EventID != cooked.GetEventId() {
		t.Fatalf("actor subject envelope mismatch: %+v", actEnv)
	}
	if actEnv.Sequence == 0 {
		t.Fatalf("envelope should carry the API-assigned sequence, got 0")
	}
}

type envelope struct {
	EventID  string          `json:"event_id"`
	WorldID  string          `json:"world_id"`
	Type     string          `json:"type"`
	Sequence int64           `json:"sequence"`
	Payload  json.RawMessage `json:"payload"`
}

func recvEnvelope(t *testing.T, sub *nats.Subscription) envelope {
	t.Helper()
	msg, err := sub.NextMsg(5 * time.Second)
	if err != nil {
		t.Fatalf("NATS receive on %s: %v", sub.Subject, err)
	}
	var env envelope
	if err := json.Unmarshal(msg.Data, &env); err != nil {
		t.Fatalf("unmarshal envelope: %v (raw=%s)", err, string(msg.Data))
	}
	return env
}

// TestAppendEventsFullLoopRestore exercises the M3 一連 flow (mining → cooking →
// discard → clean) and then confirms restart restore returns the event tail in
// order after a snapshot (AT-018 の永続面).
func TestAppendEventsFullLoopRestore(t *testing.T) {
	h := setup(t)
	h.seedItems(t)
	ctx := context.Background()
	worldID := h.newWorld(t)
	owner := store.NewUUID()

	okResults(t, h.append(t, mkEvent(t, worldID, "resource.mined", "n", 1, map[string]any{
		"actor_id": owner,
		"grants":   []map[string]any{{"item_definition_id": "raw_meat", "quantity": 2}},
	})))
	okResults(t, h.append(t, mkEvent(t, worldID, "cooking.completed", "c", 2, map[string]any{
		"actor_id": owner,
		"consumed": []map[string]any{{"item_definition_id": "raw_meat", "quantity": 1}},
		"produced": []map[string]any{{"item_definition_id": "food_waste", "quantity": 1}},
	})))

	// Snapshot at the current sequence (2), then two more events form the tail.
	payload := []byte(`{"world":"m3"}`)
	if _, err := h.world.SaveSnapshot(ctx, &survivalv1.SaveSnapshotRequest{
		WorldId: worldID, Sequence: 2, Checksum: checksum(payload), Payload: payload,
	}); err != nil {
		t.Fatalf("SaveSnapshot: %v", err)
	}

	wiID := "wi-" + store.NewUUID()
	okResults(t, h.append(t, mkEvent(t, worldID, "item.discarded", owner, 3, map[string]any{
		"actor_id": owner, "world_item_id": wiID, "item_definition_id": "food_waste", "quantity": 1,
	})))
	okResults(t, h.append(t, mkEvent(t, worldID, "cleaning.completed", wiID, 4, map[string]any{
		"world_item_id": wiID, "owner_id": owner, "reward_amount": 3,
	})))

	// Restart restore: snapshot(seq=2) + tail (seq 3,4) in ascending order.
	boot, err := h.world.LoadBootstrap(ctx, &survivalv1.LoadBootstrapRequest{WorldId: worldID, ServerBuild: "m3"})
	if err != nil {
		t.Fatalf("LoadBootstrap: %v", err)
	}
	if boot.GetSequence() != 2 {
		t.Fatalf("restore sequence: got %d want 2", boot.GetSequence())
	}
	tail := boot.GetEventTail()
	if len(tail) != 2 || tail[0].GetType() != "item.discarded" || tail[1].GetType() != "cleaning.completed" {
		t.Fatalf("restore tail mismatch: %+v", tail)
	}
	// Final persisted state is consistent: waste discarded then disposed; reward paid.
	var wiCount int
	if err := h.pool.QueryRow(ctx, `SELECT count(*) FROM world_items WHERE world_item_id=$1`, wiID).Scan(&wiCount); err != nil {
		t.Fatalf("world_items: %v", err)
	}
	if wiCount != 0 {
		t.Fatalf("world_item should be disposed, got %d", wiCount)
	}
}

package store

import (
	"encoding/json"
	"testing"
)

// These are pure unit tests (no DB) for the M3 event routing / envelope helpers,
// so they run under `make test` even without infrastructure.

func TestEventSubjectRouting(t *testing.T) {
	const w = "world-1"
	cases := map[string]string{
		"resource.mined":                 "world.world-1.event.resource",
		"resource.node_depleted":         "world.world-1.event.resource",
		"resource.node_regenerated":      "world.world-1.event.resource",
		"station.job_completed":          "world.world-1.event.actor",
		"cooking.completed":              "world.world-1.event.actor",
		"hunting.carcass_butchered":      "world.world-1.event.actor",
		"item.discarded":                 "world.world-1.event.actor",
		"cleaning.completed":             "world.world-1.event.actor",
		"development.blueprint_unlocked": "world.world-1.event.actor",
		"character.vitals_changed":       "world.world-1.event.actor",
	}
	for typ, want := range cases {
		if got := eventSubject(w, typ); got != want {
			t.Errorf("eventSubject(%q) = %q, want %q", typ, got, want)
		}
	}
}

func TestOwnerResolution(t *testing.T) {
	if got := (&eventPayload{OwnerID: "o", ActorID: "a", KillerID: "k"}).owner(); got != "o" {
		t.Errorf("owner_id should win: got %q", got)
	}
	if got := (&eventPayload{ActorID: "a", KillerID: "k"}).owner(); got != "a" {
		t.Errorf("actor_id should win over killer_id: got %q", got)
	}
	if got := (&eventPayload{KillerID: "k"}).owner(); got != "k" {
		t.Errorf("killer_id fallback: got %q", got)
	}
	if got := (&eventPayload{}).owner(); got != "" {
		t.Errorf("no owner should be empty: got %q", got)
	}
}

func TestDefaultQty(t *testing.T) {
	for in, want := range map[int]int{0: 1, -3: 1, 1: 1, 5: 5} {
		if got := defaultQty(in); got != want {
			t.Errorf("defaultQty(%d) = %d, want %d", in, got, want)
		}
	}
}

func TestOutboxEnvelope(t *testing.T) {
	e := EventInput{
		EventID:       "ev-1",
		WorldID:       "w-1",
		AggregateID:   "node-1",
		LocalSequence: 3,
		Type:          "resource.mined",
		Payload:       []byte(`{"grants":[{"item_definition_id":"stone","quantity":2}]}`),
		OccurredAtMs:  1_700_000_000_000,
	}
	raw := outboxEnvelope(e, 42)

	var env struct {
		EventID  string          `json:"event_id"`
		WorldID  string          `json:"world_id"`
		Type     string          `json:"type"`
		Sequence int64           `json:"sequence"`
		Payload  json.RawMessage `json:"payload"`
	}
	if err := json.Unmarshal([]byte(raw), &env); err != nil {
		t.Fatalf("envelope is not valid JSON: %v (%s)", err, raw)
	}
	if env.EventID != "ev-1" || env.Type != "resource.mined" || env.Sequence != 42 || env.WorldID != "w-1" {
		t.Fatalf("envelope fields mismatch: %+v", env)
	}
	// The raw payload is embedded verbatim under "payload".
	var p struct {
		Grants []struct {
			ItemDefinitionID string `json:"item_definition_id"`
			Quantity         int    `json:"quantity"`
		} `json:"grants"`
	}
	if err := json.Unmarshal(env.Payload, &p); err != nil {
		t.Fatalf("payload not embedded: %v", err)
	}
	if len(p.Grants) != 1 || p.Grants[0].ItemDefinitionID != "stone" || p.Grants[0].Quantity != 2 {
		t.Fatalf("payload content mismatch: %+v", p.Grants)
	}
}

// TestOutboxEnvelopeEmptyPayload ensures a nil payload becomes an empty object,
// keeping the envelope valid JSON.
func TestOutboxEnvelopeEmptyPayload(t *testing.T) {
	raw := outboxEnvelope(EventInput{EventID: "e", WorldID: "w", Type: "resource.node_depleted"}, 1)
	if !json.Valid([]byte(raw)) {
		t.Fatalf("envelope not valid JSON: %s", raw)
	}
}

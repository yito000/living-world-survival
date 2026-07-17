package worldevent

import (
	"context"
	"encoding/json"
	"testing"
	"time"
)

// fakeStore stands in for the Postgres-backed store so the 10.4 approval rules
// can be tested without infra.
type fakeStore struct {
	activeInRegion int
	lastAt         time.Time
	lastOK         bool
	templateActive bool
	registered     []string
}

func (f *fakeStore) RegisterWorldEvent(_ context.Context, proposalID, _, _, _ string, _ []byte) (string, error) {
	f.registered = append(f.registered, proposalID)
	return "instance-" + proposalID, nil
}

func (f *fakeStore) CountActiveInRegion(context.Context, string, string) (int, error) {
	return f.activeInRegion, nil
}

func (f *fakeStore) LastWorldEventAt(context.Context, string, string) (time.Time, bool, error) {
	return f.lastAt, f.lastOK, nil
}

func (f *fakeStore) ActiveTemplateVersion(context.Context, string) (int32, bool, error) {
	if !f.templateActive {
		return 0, false, nil
	}
	return 1, true, nil
}

func okStore() *fakeStore { return &fakeStore{templateActive: true} }

func proposal(templateID string, params string) Proposal {
	return Proposal{
		ProposalID: "p1",
		TemplateID: templateID,
		WorldID:    "w1",
		RegionID:   "r1",
		Params:     json.RawMessage(params),
		Score:      0.5,
	}
}

func TestCheckApprovesCleanProposal(t *testing.T) {
	a := &Approver{Store: okStore()}
	reason, err := a.check(context.Background(), proposal("world_event.great_hunt", `{"requested_intensity":0.5}`))
	if err != nil {
		t.Fatalf("check: %v", err)
	}
	if reason != "" {
		t.Fatalf("want approval, got reason %q", reason)
	}
}

func TestCheckRejectsUnknownTemplate(t *testing.T) {
	// Allowed ID 検証（17章 MVP-SEC-008）: the LLM cannot invent an event template.
	a := &Approver{Store: okStore()}
	reason, err := a.check(context.Background(), proposal("world_event.meteor_strike", `{}`))
	if err != nil {
		t.Fatalf("check: %v", err)
	}
	if reason != ReasonUnknownTemplate {
		t.Fatalf("reason = %q, want %q", reason, ReasonUnknownTemplate)
	}
}

func TestCheckRejectsInactiveTemplate(t *testing.T) {
	a := &Approver{Store: &fakeStore{templateActive: false}}
	reason, err := a.check(context.Background(), proposal("world_event.great_hunt", `{}`))
	if err != nil {
		t.Fatalf("check: %v", err)
	}
	if reason != ReasonTemplateInactive {
		t.Fatalf("reason = %q, want %q", reason, ReasonTemplateInactive)
	}
}

func TestCheckRejectsRegionConflict(t *testing.T) {
	// 同一 Region に既に live なイベントがあれば入場させない（10.4）。
	st := okStore()
	st.activeInRegion = 1
	a := &Approver{Store: st}
	reason, err := a.check(context.Background(), proposal("world_event.great_hunt", `{}`))
	if err != nil {
		t.Fatalf("check: %v", err)
	}
	if reason != ReasonRegionConflict {
		t.Fatalf("reason = %q, want %q", reason, ReasonRegionConflict)
	}
}

func TestCheckRejectsWithinCooldown(t *testing.T) {
	st := okStore()
	st.lastOK = true
	st.lastAt = time.Now().Add(-1 * time.Minute) // great_hunt cooldown is 15m
	a := &Approver{Store: st}
	reason, err := a.check(context.Background(), proposal("world_event.great_hunt", `{}`))
	if err != nil {
		t.Fatalf("check: %v", err)
	}
	if reason != ReasonCooldown {
		t.Fatalf("reason = %q, want %q", reason, ReasonCooldown)
	}
}

func TestCheckAllowsAfterCooldown(t *testing.T) {
	st := okStore()
	st.lastOK = true
	st.lastAt = time.Now().Add(-20 * time.Minute)
	a := &Approver{Store: st}
	reason, err := a.check(context.Background(), proposal("world_event.great_hunt", `{}`))
	if err != nil {
		t.Fatalf("check: %v", err)
	}
	if reason != "" {
		t.Fatalf("want approval after cooldown, got reason %q", reason)
	}
}

func TestCheckRejectsIntensityOutOfRange(t *testing.T) {
	// requested_intensity ∈ [0,1]（付録B.2）。schema 側と二重で弾く。
	a := &Approver{Store: okStore()}
	for _, params := range []string{`{"requested_intensity":1.5}`, `{"requested_intensity":-0.2}`} {
		reason, err := a.check(context.Background(), proposal("world_event.great_hunt", params))
		if err != nil {
			t.Fatalf("check(%s): %v", params, err)
		}
		if reason != ReasonInvalidParams {
			t.Fatalf("check(%s) reason = %q, want %q", params, reason, ReasonInvalidParams)
		}
	}
}

func TestCooldownCoversEveryAllowedTemplate(t *testing.T) {
	// A template without a cooldown entry would silently get a zero window.
	for id := range AllowedTemplates {
		if Cooldowns[id] == 0 {
			t.Errorf("template %s has no cooldown configured", id)
		}
	}
}

func TestHandleRegistersApprovedProposal(t *testing.T) {
	st := okStore()
	a := &Approver{Store: st}
	body, err := json.Marshal(proposal("world_event.rare_resource", `{"requested_intensity":0.3}`))
	if err != nil {
		t.Fatalf("marshal: %v", err)
	}
	a.handle(context.Background(), body)
	if len(st.registered) != 1 || st.registered[0] != "p1" {
		t.Fatalf("registered = %v, want [p1]", st.registered)
	}
}

func TestHandleDropsMalformedAndIncompleteProposals(t *testing.T) {
	st := okStore()
	a := &Approver{Store: st}
	a.handle(context.Background(), []byte("not json"))
	a.handle(context.Background(), []byte(`{"template_id":"world_event.great_hunt"}`)) // no proposal_id/world_id
	if len(st.registered) != 0 {
		t.Fatalf("registered = %v, want none", st.registered)
	}
}

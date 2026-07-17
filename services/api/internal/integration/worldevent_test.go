package integration

import (
	"context"
	"encoding/json"
	"testing"

	"living-world-survival/services/api/internal/store"
	"living-world-survival/services/api/internal/worldevent"
	survivalv1 "living-world-survival/services/gen/go/survival/v1"
)

// M5 AT-015/016/017 の状態遷移整合（08B 5章）。Spawn cap / 供給予算の強制は DS 側
// （08A 3.2）だが、PROPOSED→ACTIVE→COMPLETED の遷移と冪等性はここで担保する。

func registerTestEvent(t *testing.T, h *harness, srv *worldevent.Server, proposalID string) string {
	t.Helper()
	worldID := h.newWorld(t)
	resp, err := srv.Register(context.Background(), &survivalv1.RegisterRequest{
		ProposalId: proposalID,
		TemplateId: "world_event.great_hunt",
		WorldId:    worldID,
		RegionId:   "region-a",
		Params:     []byte(`{"requested_intensity":0.5}`),
	})
	if err != nil {
		t.Fatalf("Register: %v", err)
	}
	if resp.GetEventInstanceId() == "" {
		t.Fatal("Register returned an empty event_instance_id")
	}
	t.Cleanup(func() {
		_, _ = h.pool.Exec(context.Background(),
			`DELETE FROM world_event_instances WHERE proposal_id = $1`, proposalID)
	})
	return resp.GetEventInstanceId()
}

func TestRegisterIsIdempotentOnProposalID(t *testing.T) {
	h := setup(t)
	srv := &worldevent.Server{Store: h.store}
	proposalID := "prop-" + store.NewUUID()

	first := registerTestEvent(t, h, srv, proposalID)

	// 同一提案の再登録（redelivery）は既存 id を返し、2 個目のイベントを生まない。
	second, err := srv.Register(context.Background(), &survivalv1.RegisterRequest{
		ProposalId: proposalID,
		TemplateId: "world_event.great_hunt",
		WorldId:    h.newWorld(t),
		RegionId:   "region-b",
		Params:     []byte(`{"requested_intensity":0.9}`),
	})
	if err != nil {
		t.Fatalf("Register (repeat): %v", err)
	}
	if second.GetEventInstanceId() != first {
		t.Fatalf("re-register returned %s, want the existing %s", second.GetEventInstanceId(), first)
	}

	var n int
	if err := h.pool.QueryRow(context.Background(),
		`SELECT count(*) FROM world_event_instances WHERE proposal_id = $1`, proposalID).Scan(&n); err != nil {
		t.Fatalf("count: %v", err)
	}
	if n != 1 {
		t.Fatalf("row count = %d, want 1", n)
	}
}

func TestRegisterStartsProposed(t *testing.T) {
	h := setup(t)
	srv := &worldevent.Server{Store: h.store}
	id := registerTestEvent(t, h, srv, "prop-"+store.NewUUID())

	inst, err := h.store.GetWorldEvent(context.Background(), id)
	if err != nil {
		t.Fatalf("GetWorldEvent: %v", err)
	}
	if inst.State != store.WorldEventStateProposed {
		t.Fatalf("state = %d, want PROPOSED(%d)", inst.State, store.WorldEventStateProposed)
	}
}

func TestUpdateStateProposedToActiveToCompleted(t *testing.T) {
	h := setup(t)
	srv := &worldevent.Server{Store: h.store}
	id := registerTestEvent(t, h, srv, "prop-"+store.NewUUID())
	ctx := context.Background()

	resp, err := srv.UpdateState(ctx, &survivalv1.UpdateStateRequest{
		EventInstanceId: id,
		ExpectedState:   survivalv1.WorldEventState_WORLD_EVENT_STATE_PROPOSED,
		NewState:        survivalv1.WorldEventState_WORLD_EVENT_STATE_ACTIVE,
	})
	if err != nil {
		t.Fatalf("UpdateState(ACTIVE): %v", err)
	}
	if resp.GetStatus() != survivalv1.ResultStatus_RESULT_STATUS_OK {
		t.Fatalf("status = %v, want OK", resp.GetStatus())
	}

	stats := []byte(`{"spawned":40,"harvested":12,"purchased":0,"remaining":0,"participant_count":7}`)
	resp, err = srv.UpdateState(ctx, &survivalv1.UpdateStateRequest{
		EventInstanceId: id,
		ExpectedState:   survivalv1.WorldEventState_WORLD_EVENT_STATE_ACTIVE,
		NewState:        survivalv1.WorldEventState_WORLD_EVENT_STATE_COMPLETED,
		Stats:           stats,
	})
	if err != nil {
		t.Fatalf("UpdateState(COMPLETED): %v", err)
	}
	if resp.GetStatus() != survivalv1.ResultStatus_RESULT_STATUS_OK {
		t.Fatalf("status = %v, want OK", resp.GetStatus())
	}

	inst, err := h.store.GetWorldEvent(ctx, id)
	if err != nil {
		t.Fatalf("GetWorldEvent: %v", err)
	}
	if inst.State != store.WorldEventStateCompleted {
		t.Fatalf("state = %d, want COMPLETED(%d)", inst.State, store.WorldEventStateCompleted)
	}
	var got map[string]int
	if err := json.Unmarshal(inst.Stats, &got); err != nil {
		t.Fatalf("stats unmarshal: %v", err)
	}
	if got["spawned"] != 40 || got["participant_count"] != 7 {
		t.Fatalf("stats = %v, want the end-of-event aggregate", got)
	}
}

func TestUpdateStateConflictsOnExpectedStateMismatch(t *testing.T) {
	h := setup(t)
	srv := &worldevent.Server{Store: h.store}
	id := registerTestEvent(t, h, srv, "prop-"+store.NewUUID())
	ctx := context.Background()

	// 実状態は PROPOSED。ACTIVE を期待した遷移は条件不一致で CONFLICT（二重遷移防止）。
	resp, err := srv.UpdateState(ctx, &survivalv1.UpdateStateRequest{
		EventInstanceId: id,
		ExpectedState:   survivalv1.WorldEventState_WORLD_EVENT_STATE_ACTIVE,
		NewState:        survivalv1.WorldEventState_WORLD_EVENT_STATE_COMPLETED,
	})
	if err != nil {
		t.Fatalf("UpdateState: unexpected transport error: %v", err)
	}
	if resp.GetStatus() != survivalv1.ResultStatus_RESULT_STATUS_CONFLICT {
		t.Fatalf("status = %v, want CONFLICT", resp.GetStatus())
	}

	inst, err := h.store.GetWorldEvent(ctx, id)
	if err != nil {
		t.Fatalf("GetWorldEvent: %v", err)
	}
	if inst.State != store.WorldEventStateProposed {
		t.Fatalf("state = %d, want unchanged PROPOSED(%d)", inst.State, store.WorldEventStateProposed)
	}
}

func TestUpdateStateIsIdempotentUnderRetry(t *testing.T) {
	h := setup(t)
	srv := &worldevent.Server{Store: h.store}
	id := registerTestEvent(t, h, srv, "prop-"+store.NewUUID())
	ctx := context.Background()

	req := &survivalv1.UpdateStateRequest{
		EventInstanceId: id,
		ExpectedState:   survivalv1.WorldEventState_WORLD_EVENT_STATE_PROPOSED,
		NewState:        survivalv1.WorldEventState_WORLD_EVENT_STATE_ACTIVE,
	}
	if _, err := srv.UpdateState(ctx, req); err != nil {
		t.Fatalf("UpdateState: %v", err)
	}
	// 同じ要求の再送は CONFLICT を返すが、状態は ACTIVE のまま壊れない。
	resp, err := srv.UpdateState(ctx, req)
	if err != nil {
		t.Fatalf("UpdateState (retry): %v", err)
	}
	if resp.GetStatus() != survivalv1.ResultStatus_RESULT_STATUS_CONFLICT {
		t.Fatalf("retry status = %v, want CONFLICT", resp.GetStatus())
	}
	inst, err := h.store.GetWorldEvent(ctx, id)
	if err != nil {
		t.Fatalf("GetWorldEvent: %v", err)
	}
	if inst.State != store.WorldEventStateActive {
		t.Fatalf("state = %d, want ACTIVE(%d)", inst.State, store.WorldEventStateActive)
	}
}

func TestUpdateStateUnknownInstanceIsNotFound(t *testing.T) {
	h := setup(t)
	srv := &worldevent.Server{Store: h.store}
	_, err := srv.UpdateState(context.Background(), &survivalv1.UpdateStateRequest{
		EventInstanceId: store.NewUUID(),
		ExpectedState:   survivalv1.WorldEventState_WORLD_EVENT_STATE_PROPOSED,
		NewState:        survivalv1.WorldEventState_WORLD_EVENT_STATE_ACTIVE,
	})
	if err == nil {
		t.Fatal("UpdateState on an unknown instance: want an error, got nil")
	}
}

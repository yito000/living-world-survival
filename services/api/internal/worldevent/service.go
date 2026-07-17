// Package worldevent implements the internal WorldEventService (MVP 14.2 /
// worldevent.proto) and the Event Proposal approval path (MVP 10.4). The API is
// the single authoritative Writer of world_event_instances (13章 / 付録C): the
// Director proposes, this package decides and records, and the DS drives the
// local progression via UpdateState.
package worldevent

import (
	"context"
	"errors"
	"sync/atomic"

	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"

	"living-world-survival/services/api/internal/store"
	"living-world-survival/services/common/obs"
	survivalv1 "living-world-survival/services/gen/go/survival/v1"
)

// Server implements survivalv1.WorldEventServiceServer.
type Server struct {
	survivalv1.UnimplementedWorldEventServiceServer
	Store *store.Store

	// results publishes worldevent.result messages (3.6). It is attached after
	// NATS connects, concurrently with in-flight RPCs, hence the atomic. A nil
	// publisher means transitions still commit — they are just not announced.
	results atomic.Pointer[ResultPublisher]
}

// SetResults attaches the worldevent.result publisher once NATS is connected.
func (s *Server) SetResults(p *ResultPublisher) { s.results.Store(p) }

// Register records an approved proposal as a PROPOSED instance and returns its
// event_instance_id. It is idempotent on proposal_id — re-registering the same
// proposal returns the existing id (3.5).
func (s *Server) Register(ctx context.Context, req *survivalv1.RegisterRequest) (*survivalv1.RegisterResponse, error) {
	if req.GetProposalId() == "" {
		return nil, status.Error(codes.InvalidArgument, "proposal_id is required")
	}
	if req.GetTemplateId() == "" {
		return nil, status.Error(codes.InvalidArgument, "template_id is required")
	}
	if req.GetWorldId() == "" {
		return nil, status.Error(codes.InvalidArgument, "world_id is required")
	}
	ctx = obs.WithFields(ctx, obs.Fields{WorldID: req.GetWorldId()})

	id, err := s.Store.RegisterWorldEvent(ctx,
		req.GetProposalId(), req.GetTemplateId(), req.GetWorldId(), req.GetRegionId(), req.GetParams())
	if err != nil {
		obs.L(ctx).Error("register world event failed", "error", err.Error(),
			"proposal_id", req.GetProposalId(), "template_id", req.GetTemplateId())
		return nil, status.Error(codes.Internal, "register world event failed")
	}
	return &survivalv1.RegisterResponse{EventInstanceId: id}, nil
}

// UpdateState transitions an instance conditionally on expected_state and stores
// stats. A state mismatch is NOT an error — it is a CONFLICT ResultStatus, so a
// duplicate/late transition from a retrying DS is absorbed (3.5).
//
// On a successful transition to COMPLETED it publishes worldevent.result with the
// end-of-event aggregate (3.6 終了時).
func (s *Server) UpdateState(ctx context.Context, req *survivalv1.UpdateStateRequest) (*survivalv1.UpdateStateResponse, error) {
	if req.GetEventInstanceId() == "" {
		return nil, status.Error(codes.InvalidArgument, "event_instance_id is required")
	}
	newState := int32(req.GetNewState())
	if newState == store.WorldEventStateUnspecified {
		return nil, status.Error(codes.InvalidArgument, "new_state must not be UNSPECIFIED")
	}

	err := s.Store.UpdateWorldEventState(ctx,
		req.GetEventInstanceId(), int32(req.GetExpectedState()), newState, req.GetStats())
	switch {
	case errors.Is(err, store.ErrNotFound):
		return nil, status.Errorf(codes.NotFound, "event instance %s not found", req.GetEventInstanceId())
	case errors.Is(err, store.ErrStateConflict):
		// Expected-state mismatch: the caller lost a race or is retrying an
		// already-applied transition. Report it in-band so the DS can re-read
		// rather than retry blindly.
		return &survivalv1.UpdateStateResponse{Status: survivalv1.ResultStatus_RESULT_STATUS_CONFLICT}, nil
	case err != nil:
		obs.L(ctx).Error("update world event state failed", "error", err.Error(),
			"event_instance_id", req.GetEventInstanceId(), "new_state", newState)
		return nil, status.Error(codes.Internal, "update world event state failed")
	}

	if results := s.results.Load(); newState == store.WorldEventStateCompleted && results != nil {
		inst, gerr := s.Store.GetWorldEvent(ctx, req.GetEventInstanceId())
		if gerr != nil {
			// The transition is committed; failing to announce it must not fail
			// the RPC (the DS has already finished the event locally).
			obs.L(ctx).Warn("load instance for completed result failed",
				"error", gerr.Error(), "event_instance_id", req.GetEventInstanceId())
		} else {
			results.PublishCompleted(ctx, inst)
		}
	}
	return &survivalv1.UpdateStateResponse{Status: survivalv1.ResultStatus_RESULT_STATUS_OK}, nil
}

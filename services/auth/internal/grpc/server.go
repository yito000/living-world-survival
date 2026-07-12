// Package grpc implements the internal MatchmakingService (MVP 11.2 / 11.3),
// used only between the Dedicated Server and Auth. It is not exposed publicly.
package grpc

import (
	"context"
	"errors"
	"log"

	"living-world-survival/services/auth/internal/store"
	"living-world-survival/services/auth/internal/ticket"
	survivalv1 "living-world-survival/services/gen/go/survival/v1"
)

// Server implements survivalv1.MatchmakingServiceServer.
type Server struct {
	survivalv1.UnimplementedMatchmakingServiceServer
	Store   *store.Store
	Tickets *ticket.Signer
}

// RedeemJoinTicket verifies the ticket signature, checks it targets this
// server, then atomically consumes it (single-use). Failures return
// ok=false with an error string rather than a gRPC error (G1, 3.4).
func (s *Server) RedeemJoinTicket(ctx context.Context, req *survivalv1.RedeemJoinTicketRequest) (*survivalv1.RedeemJoinTicketResponse, error) {
	claims, err := s.Tickets.Verify(req.GetTicket())
	if err != nil {
		return redeemErr("invalid_signature"), nil
	}
	if claims.GetServerId() != req.GetServerId() {
		return redeemErr("server_mismatch"), nil
	}
	consumed, err := s.Store.RedeemJoinTicket(ctx, claims.GetTicketId(), req.GetServerId())
	if err != nil {
		return redeemErr(redeemReason(err)), nil
	}
	// Return the DB-persisted claims (authoritative for audit).
	return &survivalv1.RedeemJoinTicketResponse{
		Ok: true,
		Claims: &survivalv1.JoinTicketClaims{
			TicketId:        consumed.TicketID,
			AccountId:       consumed.AccountID,
			CharacterId:     consumed.CharacterID,
			ServerId:        consumed.ServerID,
			WorldId:         consumed.WorldID,
			BuildId:         consumed.BuildID,
			IssuedAtUnixMs:  consumed.IssuedAt.UnixMilli(),
			ExpiresAtUnixMs: consumed.ExpiresAt.UnixMilli(),
			Nonce:           consumed.Nonce,
		},
	}, nil
}

// RegisterServer upserts a Dedicated Server (ready=false until Heartbeat) (G2).
func (s *Server) RegisterServer(ctx context.Context, req *survivalv1.RegisterServerRequest) (*survivalv1.RegisterServerResponse, error) {
	err := s.Store.UpsertServer(ctx, store.GameServer{
		ServerID: req.GetServerId(),
		WorldID:  req.GetWorldId(),
		BuildID:  req.GetBuildId(),
		Endpoint: req.GetEndpoint(),
		Capacity: req.GetCapacity(),
	})
	if err != nil {
		log.Printf("grpc: RegisterServer: %v", err)
		return &survivalv1.RegisterServerResponse{Ok: false}, nil
	}
	return &survivalv1.RegisterServerResponse{Ok: true}, nil
}

// Heartbeat refreshes liveness and the ready flag (G3).
func (s *Server) Heartbeat(ctx context.Context, req *survivalv1.HeartbeatRequest) (*survivalv1.HeartbeatResponse, error) {
	found, err := s.Store.Heartbeat(ctx, req.GetServerId(), req.GetReady())
	if err != nil {
		log.Printf("grpc: Heartbeat: %v", err)
		return &survivalv1.HeartbeatResponse{Ok: false}, nil
	}
	return &survivalv1.HeartbeatResponse{Ok: found}, nil
}

// MarkDraining takes the server out of matchmaking (G4).
func (s *Server) MarkDraining(ctx context.Context, req *survivalv1.MarkDrainingRequest) (*survivalv1.MarkDrainingResponse, error) {
	found, err := s.Store.MarkDraining(ctx, req.GetServerId())
	if err != nil {
		log.Printf("grpc: MarkDraining: %v", err)
		return &survivalv1.MarkDrainingResponse{Ok: false}, nil
	}
	return &survivalv1.MarkDrainingResponse{Ok: found}, nil
}

func redeemErr(reason string) *survivalv1.RedeemJoinTicketResponse {
	return &survivalv1.RedeemJoinTicketResponse{Ok: false, Error: reason}
}

func redeemReason(err error) string {
	switch {
	case errors.Is(err, store.ErrTicketUsed):
		return "already_used"
	case errors.Is(err, store.ErrTicketExpired):
		return "expired"
	case errors.Is(err, store.ErrServerMismatch):
		return "server_mismatch"
	case errors.Is(err, store.ErrTicketNotFound):
		return "not_found"
	default:
		log.Printf("grpc: RedeemJoinTicket: %v", err)
		return "internal"
	}
}

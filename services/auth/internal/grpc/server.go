// Package grpc implements the internal MatchmakingService (MVP 11.2 / 11.3),
// used only between the Dedicated Server and Auth. It is not exposed publicly.
package grpc

import (
	"context"
	"errors"

	"living-world-survival/services/auth/internal/metrics"
	"living-world-survival/services/auth/internal/store"
	"living-world-survival/services/auth/internal/ticket"
	"living-world-survival/services/common/obs"
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
	ctx = obs.WithFields(ctx, obs.Fields{ServerID: req.GetServerId()})

	claims, err := s.Tickets.Verify(req.GetTicket())
	if err != nil {
		// 署名不正はチケット本体を出さない（MVP-SEC-002）。
		return s.redeemRejected(ctx, "invalid_signature"), nil
	}
	ctx = obs.WithFields(ctx, obs.Fields{
		AccountID: claims.GetAccountId(), WorldID: claims.GetWorldId(),
	})
	if claims.GetServerId() != req.GetServerId() {
		return s.redeemRejected(ctx, "server_mismatch"), nil
	}
	consumed, err := s.Store.RedeemJoinTicket(ctx, claims.GetTicketId(), req.GetServerId())
	if err != nil {
		return s.redeemRejected(ctx, s.redeemReason(ctx, err)), nil
	}

	// Ticket 消費は監査対象（MVP-SEC-009）。
	metrics.JoinTicketRedeems.WithLabelValues("ok").Inc()
	obs.L(ctx).Info("join ticket redeemed",
		"audit", true, "ticket_id", consumed.TicketID,
		"character_id", consumed.CharacterID, "build_id", consumed.BuildID)

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
	ctx = obs.WithFields(ctx, obs.Fields{
		ServerID: req.GetServerId(), WorldID: req.GetWorldId(),
	})
	err := s.Store.UpsertServer(ctx, store.GameServer{
		ServerID: req.GetServerId(),
		WorldID:  req.GetWorldId(),
		BuildID:  req.GetBuildId(),
		Endpoint: req.GetEndpoint(),
		Capacity: req.GetCapacity(),
	})
	if err != nil {
		obs.L(ctx).Error("register server failed", "error", err.Error())
		return &survivalv1.RegisterServerResponse{Ok: false}, nil
	}
	obs.L(ctx).Info("server registered",
		"build_id", req.GetBuildId(), "capacity", req.GetCapacity())
	return &survivalv1.RegisterServerResponse{Ok: true}, nil
}

// Heartbeat refreshes liveness and the ready flag (G3), and records the DS
// self-reported tick/player metrics（10B 3.1・3.5: Tick の出所は DS に統一）。
func (s *Server) Heartbeat(ctx context.Context, req *survivalv1.HeartbeatRequest) (*survivalv1.HeartbeatResponse, error) {
	serverID := req.GetServerId()
	ctx = obs.WithFields(ctx, obs.Fields{ServerID: serverID})

	found, err := s.Store.Heartbeat(ctx, serverID, req.GetReady())
	if err != nil {
		obs.L(ctx).Error("heartbeat failed", "error", err.Error())
		return &survivalv1.HeartbeatResponse{Ok: false}, nil
	}
	if !found {
		// 未登録 server の Heartbeat で系列を作ると、存在しない DS の
		// tick/players がスクレイプ結果に混ざる。
		return &survivalv1.HeartbeatResponse{Ok: false}, nil
	}

	metrics.DSHeartbeats.WithLabelValues(serverID).Inc()
	metrics.ObserveTickMS(serverID, req.GetTickMs())
	metrics.SetPlayers(serverID, req.GetPlayers())
	metrics.SetReady(serverID, req.GetReady())

	return &survivalv1.HeartbeatResponse{Ok: true}, nil
}

// MarkDraining takes the server out of matchmaking (G4).
func (s *Server) MarkDraining(ctx context.Context, req *survivalv1.MarkDrainingRequest) (*survivalv1.MarkDrainingResponse, error) {
	serverID := req.GetServerId()
	ctx = obs.WithFields(ctx, obs.Fields{ServerID: serverID})

	found, err := s.Store.MarkDraining(ctx, serverID)
	if err != nil {
		obs.L(ctx).Error("mark draining failed", "error", err.Error())
		return &survivalv1.MarkDrainingResponse{Ok: false}, nil
	}
	if found {
		// ドレイン後は Matchmaking 対象外。ready 系列を 0 に落としておかないと、
		// 停止した DS が ready のまま残って見える。
		metrics.SetReady(serverID, false)
		obs.L(ctx).Info("server draining")
	}
	return &survivalv1.MarkDrainingResponse{Ok: found}, nil
}

// redeemRejected は拒否理由を metrics と監査ログへ残しつつ応答を組む
// （MVP-SEC-004 の期限切れ/再利用/不一致拒否を観測可能にする）。
func (s *Server) redeemRejected(ctx context.Context, reason string) *survivalv1.RedeemJoinTicketResponse {
	metrics.JoinTicketRedeems.WithLabelValues(reason).Inc()
	obs.L(ctx).Warn("join ticket rejected", "audit", true, "reason", reason)
	return &survivalv1.RedeemJoinTicketResponse{Ok: false, Error: reason}
}

func (s *Server) redeemReason(ctx context.Context, err error) string {
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
		obs.L(ctx).Error("redeem join ticket failed", "error", err.Error())
		return "internal"
	}
}

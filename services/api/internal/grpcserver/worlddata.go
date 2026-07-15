// Package grpcserver implements the internal WorldDataService and
// ActorStateService (MVP 12.1 / 14.2), used only between the Dedicated Server
// and the API. The API is the single authoritative Writer of world snapshots,
// domain events and inventories (MVP 12.2.1).
package grpcserver

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"log"
	"strings"

	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"

	"living-world-survival/services/api/internal/store"
	survivalv1 "living-world-survival/services/gen/go/survival/v1"
)

// WorldDataServer implements survivalv1.WorldDataServiceServer.
type WorldDataServer struct {
	survivalv1.UnimplementedWorldDataServiceServer
	Store *store.Store
}

// LoadBootstrap returns the active snapshot for a world plus the tail of domain
// events with sequence greater than the snapshot's (3.2). The snapshot payload
// is returned as opaque bytes — the DS restores it. A missing world is NOT_FOUND.
func (s *WorldDataServer) LoadBootstrap(ctx context.Context, req *survivalv1.LoadBootstrapRequest) (*survivalv1.LoadBootstrapResponse, error) {
	worldID := req.GetWorldId()
	if worldID == "" {
		return nil, status.Error(codes.InvalidArgument, "world_id is required")
	}

	boot, err := s.Store.LoadWorldBootstrap(ctx, worldID)
	if errors.Is(err, store.ErrNotFound) {
		return nil, status.Errorf(codes.NotFound, "world %s not found", worldID)
	}
	if err != nil {
		log.Printf("grpc: LoadBootstrap: %v", err)
		return nil, status.Error(codes.Internal, "load bootstrap failed")
	}

	tail, err := s.Store.LoadEventTail(ctx, worldID, boot.Sequence)
	if err != nil {
		log.Printf("grpc: LoadBootstrap tail: %v", err)
		return nil, status.Error(codes.Internal, "load event tail failed")
	}

	// Master data (Item/Recipe/ResourceNodeDef) is delivered to the DS in the
	// bootstrap so it caches the authoritative quantities/recipes (3.4 / 06A 0.4-2).
	master, err := s.Store.LoadMasterData(ctx, worldID)
	if err != nil {
		log.Printf("grpc: LoadBootstrap master data: %v", err)
		return nil, status.Error(codes.Internal, "load master data failed")
	}
	masterJSON, err := json.Marshal(master)
	if err != nil {
		log.Printf("grpc: LoadBootstrap marshal master: %v", err)
		return nil, status.Error(codes.Internal, "marshal master data failed")
	}

	resp := &survivalv1.LoadBootstrapResponse{
		SnapshotId:      boot.SnapshotID,
		Sequence:        boot.Sequence,
		SnapshotPayload: boot.Payload,
		EventTail:       make([]*survivalv1.DomainEvent, 0, len(tail)),
		MasterData:      masterJSON,
	}
	for _, t := range tail {
		resp.EventTail = append(resp.EventTail, &survivalv1.DomainEvent{
			EventId:          t.EventID,
			WorldId:          t.WorldID,
			AggregateId:      t.AggregateID,
			LocalSequence:    t.LocalSequence,
			Type:             t.Type,
			Payload:          t.Payload,
			OccurredAtUnixMs: t.OccurredAt.UnixMilli(),
		})
	}
	// server_build is recorded for future compatibility checks (3.2 step 4).
	if b := req.GetServerBuild(); b != "" {
		log.Printf("grpc: LoadBootstrap world=%s server_build=%s tail=%d", worldID, b, len(tail))
	}
	return resp, nil
}

// AppendEvents persists a batch of domain events, deduplicating by event_id and
// assigning the world-wide sequence on the API side (3.3). Each event gets an
// independent ResultStatus in the same order as the request.
func (s *WorldDataServer) AppendEvents(ctx context.Context, req *survivalv1.AppendEventsRequest) (*survivalv1.AppendEventsResponse, error) {
	protoEvents := req.GetEvents()
	inputs := make([]store.EventInput, 0, len(protoEvents))
	for _, e := range protoEvents {
		inputs = append(inputs, store.EventInput{
			EventID:       e.GetEventId(),
			WorldID:       e.GetWorldId(),
			AggregateID:   e.GetAggregateId(),
			LocalSequence: e.GetLocalSequence(),
			Type:          e.GetType(),
			Payload:       e.GetPayload(),
			OccurredAtMs:  e.GetOccurredAtUnixMs(),
		})
	}

	outcomes, err := s.Store.AppendEvents(ctx, inputs)
	if err != nil {
		log.Printf("grpc: AppendEvents (server=%s): %v", req.GetServerId(), err)
		return nil, status.Error(codes.Internal, "append events failed")
	}

	results := make([]survivalv1.ResultStatus, 0, len(outcomes))
	for _, o := range outcomes {
		switch o {
		case store.AppendOK:
			results = append(results, survivalv1.ResultStatus_RESULT_STATUS_OK)
		case store.AppendDuplicate:
			results = append(results, survivalv1.ResultStatus_RESULT_STATUS_DUPLICATE)
		case store.AppendConflict:
			results = append(results, survivalv1.ResultStatus_RESULT_STATUS_CONFLICT)
		default:
			results = append(results, survivalv1.ResultStatus_RESULT_STATUS_UNSPECIFIED)
		}
	}
	return &survivalv1.AppendEventsResponse{Results: results}, nil
}

// SaveSnapshot verifies the payload checksum, then stages the snapshot and flips
// the world's active pointer in one transaction (3.4). A checksum mismatch is
// rejected with INVALID_ARGUMENT and never touches the active pointer.
func (s *WorldDataServer) SaveSnapshot(ctx context.Context, req *survivalv1.SaveSnapshotRequest) (*survivalv1.SaveSnapshotResponse, error) {
	worldID := req.GetWorldId()
	if worldID == "" {
		return nil, status.Error(codes.InvalidArgument, "world_id is required")
	}

	if !checksumMatches(req.GetPayload(), req.GetChecksum()) {
		return nil, status.Error(codes.InvalidArgument, "snapshot checksum mismatch")
	}

	snapshotID, err := s.Store.SaveSnapshot(ctx, worldID, req.GetSequence(), req.GetChecksum(), req.GetPayload())
	if errors.Is(err, store.ErrNotFound) {
		return nil, status.Errorf(codes.NotFound, "world %s not found", worldID)
	}
	if err != nil {
		log.Printf("grpc: SaveSnapshot: %v", err)
		return nil, status.Error(codes.Internal, "save snapshot failed")
	}
	return &survivalv1.SaveSnapshotResponse{SnapshotId: snapshotID}, nil
}

// checksumMatches computes the SHA-256 hex of the payload bytes and compares it
// case-insensitively with the supplied checksum. The algorithm and target byte
// range MUST match the DS generator (05A, 落とし穴6.4): lowercase hex SHA-256 of
// the raw payload bytes. EqualFold tolerates upper/lower-case hex differences.
func checksumMatches(payload []byte, checksum string) bool {
	if checksum == "" {
		return false
	}
	sum := sha256.Sum256(payload)
	return strings.EqualFold(checksum, hex.EncodeToString(sum[:]))
}

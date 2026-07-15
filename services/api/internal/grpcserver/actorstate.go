package grpcserver

import (
	"context"
	"encoding/json"
	"errors"
	"log"

	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"

	"living-world-survival/services/api/internal/store"
	survivalv1 "living-world-survival/services/gen/go/survival/v1"
)

// ActorStateServer implements survivalv1.ActorStateServiceServer.
type ActorStateServer struct {
	survivalv1.UnimplementedActorStateServiceServer
	Store *store.Store
}

// inventorySummaryEntry is the persisted shape of an InventoryEntry summary.
// This is only a runtime-state summary; the authoritative inventory_entries
// table is written elsewhere (API purchase path, M6) — NOT here (3.5 step 3).
type inventorySummaryEntry struct {
	SlotIndex        int32  `json:"slot_index"`
	ItemDefinitionID string `json:"item_definition_id"`
	ItemInstanceID   string `json:"item_instance_id,omitempty"`
	Quantity         int32  `json:"quantity"`
	Reserved         int32  `json:"reserved"`
}

// Save persists an actor's runtime state with a monotonic version (3.5). The
// personal_state (JSON bytes) is stored as the runtime payload; the
// inventory_summary is folded into that payload as a summary. A version that is
// not newer than the stored one is ignored and reported as DUPLICATE so a
// late/duplicate save never overwrites newer state.
func (s *ActorStateServer) Save(ctx context.Context, req *survivalv1.SaveRequest) (*survivalv1.SaveResponse, error) {
	actorID := req.GetActorId()
	if actorID == "" {
		return nil, status.Error(codes.InvalidArgument, "actor_id is required")
	}

	payload, worldID := s.buildPayload(ctx, actorID, req)
	if worldID == "" {
		// world_id is required (NOT NULL) and could not be resolved.
		log.Printf("grpc: ActorState.Save actor=%s: world_id unresolved", actorID)
		return &survivalv1.SaveResponse{Status: survivalv1.ResultStatus_RESULT_STATUS_REJECTED}, nil
	}

	updated, err := s.Store.SaveActorState(ctx, actorID, worldID, req.GetVersion(), payload)
	if err != nil {
		log.Printf("grpc: ActorState.Save: %v", err)
		return nil, status.Error(codes.Internal, "save actor state failed")
	}
	if !updated {
		return &survivalv1.SaveResponse{Status: survivalv1.ResultStatus_RESULT_STATUS_DUPLICATE}, nil
	}
	return &survivalv1.SaveResponse{Status: survivalv1.ResultStatus_RESULT_STATUS_OK}, nil
}

// buildPayload assembles the runtime-state payload and resolves world_id. When
// personal_state is a JSON object, the inventory_summary is merged in and
// world_id is read from it; otherwise personal_state is kept verbatim and
// world_id falls back to the previously persisted value.
func (s *ActorStateServer) buildPayload(ctx context.Context, actorID string, req *survivalv1.SaveRequest) (payload []byte, worldID string) {
	summary := make([]inventorySummaryEntry, 0, len(req.GetInventorySummary()))
	for _, e := range req.GetInventorySummary() {
		item := e.GetItem()
		summary = append(summary, inventorySummaryEntry{
			SlotIndex:        e.GetSlotIndex(),
			ItemDefinitionID: item.GetItemDefinitionId(),
			ItemInstanceID:   item.GetItemInstanceId(),
			Quantity:         e.GetQuantity(),
			Reserved:         e.GetReserved(),
		})
	}

	obj := map[string]json.RawMessage{}
	if err := json.Unmarshal(req.GetPersonalState(), &obj); err == nil {
		// personal_state is a JSON object: read world_id and fold the summary in.
		if raw, ok := obj["world_id"]; ok {
			_ = json.Unmarshal(raw, &worldID)
		}
		if summaryJSON, err := json.Marshal(summary); err == nil {
			obj["inventory_summary"] = summaryJSON
		}
		if merged, err := json.Marshal(obj); err == nil {
			payload = merged
		} else {
			payload = req.GetPersonalState()
		}
	} else {
		// Non-object personal_state: keep verbatim, resolve world_id from store.
		payload = req.GetPersonalState()
	}

	if worldID == "" {
		if existing, err := s.Store.LoadActorStateWorld(ctx, actorID); err == nil {
			worldID = existing
		} else if !errors.Is(err, store.ErrNotFound) {
			log.Printf("grpc: ActorState.Save resolve world: %v", err)
		}
	}
	return payload, worldID
}

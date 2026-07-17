package store

import (
	"context"
	"errors"
	"fmt"
	"time"

	"github.com/jackc/pgx/v5"
)

// World event instance states, mirroring the WorldEventState enum in
// worldevent.proto (08B 3.5). Stored as the raw enum number so the DB and the
// wire never drift.
const (
	WorldEventStateUnspecified int32 = 0
	WorldEventStateProposed    int32 = 1
	WorldEventStateActive      int32 = 2
	WorldEventStateCompleted   int32 = 3
	WorldEventStateRejected    int32 = 4
)

// ErrStateConflict is returned by UpdateWorldEventState when the row's current
// state does not match the caller's expected_state. The caller must map this to
// a failed ResultStatus rather than an Internal error — a losing racer and a
// duplicate transition are both normal (3.5, 冪等・二重遷移防止).
var ErrStateConflict = errors.New("store: world event state conflict")

// WorldEventInstance is a registered world event, owned by the API (付録C).
type WorldEventInstance struct {
	EventInstanceID string
	ProposalID      string
	TemplateID      string
	WorldID         string
	RegionID        string
	State           int32
	Params          []byte
	Stats           []byte
	CreatedAt       time.Time
}

// RegisterWorldEvent inserts an approved proposal as a PROPOSED instance and
// returns its event_instance_id. proposal_id is the idempotency key: re-
// registering the same proposal returns the existing id without mutating the
// row, so a redelivered approval cannot spawn a second event (3.5).
func (s *Store) RegisterWorldEvent(ctx context.Context, proposalID, templateID, worldID, regionID string, params []byte) (string, error) {
	if proposalID == "" {
		return "", fmt.Errorf("store: register world event: proposal_id is required")
	}
	id := NewUUID()
	var got string
	// DO UPDATE (not DO NOTHING) on the idempotency key so the RETURNING clause
	// yields the existing id on conflict; DO NOTHING would return no rows and
	// force a second round trip. The SET is a no-op write of the same value.
	err := s.pool.QueryRow(ctx,
		`INSERT INTO world_event_instances
		        (event_instance_id, proposal_id, template_id, world_id, region_id, state, params)
		 VALUES ($1, $2, $3, $4, NULLIF($5, ''), $6, $7::jsonb)
		 ON CONFLICT (proposal_id) DO UPDATE
		    SET proposal_id = EXCLUDED.proposal_id
		 RETURNING event_instance_id::text`,
		id, proposalID, templateID, worldID, regionID, WorldEventStateProposed, jsonbArg(params),
	).Scan(&got)
	if err != nil {
		return "", fmt.Errorf("store: register world event: %w", err)
	}
	return got, nil
}

// UpdateWorldEventState transitions an instance from expectedState to newState
// and stores stats, conditionally on the current state (optimistic condition
// update, 3.5). It returns ErrStateConflict when the row exists but is in a
// different state, and ErrNotFound when the instance does not exist.
func (s *Store) UpdateWorldEventState(ctx context.Context, instanceID string, expectedState, newState int32, stats []byte) error {
	tag, err := s.pool.Exec(ctx,
		`UPDATE world_event_instances
		    SET state = $3,
		        stats = CASE WHEN $4::jsonb = '{}'::jsonb THEN stats ELSE $4::jsonb END,
		        updated_at = now()
		  WHERE event_instance_id = $1
		    AND state = $2`,
		instanceID, expectedState, newState, jsonbArg(stats),
	)
	if err != nil {
		return fmt.Errorf("store: update world event state: %w", err)
	}
	if tag.RowsAffected() == 1 {
		return nil
	}
	// No row updated: either the instance is absent or its state differs.
	var exists bool
	if err := s.pool.QueryRow(ctx,
		`SELECT true FROM world_event_instances WHERE event_instance_id = $1`, instanceID,
	).Scan(&exists); err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return ErrNotFound
		}
		return fmt.Errorf("store: update world event state (probe): %w", err)
	}
	return ErrStateConflict
}

// GetWorldEvent loads a single instance by id. Used by the approval publisher to
// resolve world/template context for a worldevent.result message.
func (s *Store) GetWorldEvent(ctx context.Context, instanceID string) (WorldEventInstance, error) {
	var e WorldEventInstance
	var proposalID, regionID *string
	err := s.pool.QueryRow(ctx,
		`SELECT event_instance_id::text, proposal_id, template_id, world_id, region_id,
		        state, params, stats, created_at
		   FROM world_event_instances
		  WHERE event_instance_id = $1`,
		instanceID,
	).Scan(&e.EventInstanceID, &proposalID, &e.TemplateID, &e.WorldID, &regionID,
		&e.State, &e.Params, &e.Stats, &e.CreatedAt)
	if errors.Is(err, pgx.ErrNoRows) {
		return WorldEventInstance{}, ErrNotFound
	}
	if err != nil {
		return WorldEventInstance{}, fmt.Errorf("store: get world event: %w", err)
	}
	if proposalID != nil {
		e.ProposalID = *proposalID
	}
	if regionID != nil {
		e.RegionID = *regionID
	}
	return e, nil
}

// CountActiveInRegion returns how many instances in the region are currently
// PROPOSED or ACTIVE. Used by the 10.4 same-region conflict check.
func (s *Store) CountActiveInRegion(ctx context.Context, worldID, regionID string) (int, error) {
	var n int
	err := s.pool.QueryRow(ctx,
		`SELECT count(*) FROM world_event_instances
		  WHERE world_id = $1 AND region_id IS NOT DISTINCT FROM NULLIF($2, '')
		    AND state = ANY($3::int[])`,
		worldID, regionID, []int32{WorldEventStateProposed, WorldEventStateActive},
	).Scan(&n)
	if err != nil {
		return 0, fmt.Errorf("store: count active in region: %w", err)
	}
	return n, nil
}

// LastWorldEventAt returns when an instance of templateID was last created in
// the world, and whether any exists. Used by the 10.4 same-kind cooldown check.
func (s *Store) LastWorldEventAt(ctx context.Context, worldID, templateID string) (time.Time, bool, error) {
	var at time.Time
	err := s.pool.QueryRow(ctx,
		`SELECT created_at FROM world_event_instances
		  WHERE world_id = $1 AND template_id = $2
		  ORDER BY created_at DESC LIMIT 1`,
		worldID, templateID,
	).Scan(&at)
	if errors.Is(err, pgx.ErrNoRows) {
		return time.Time{}, false, nil
	}
	if err != nil {
		return time.Time{}, false, fmt.Errorf("store: last world event at: %w", err)
	}
	return at, true, nil
}

// ActiveTemplateVersion returns the active version of an action_templates row,
// and whether the template is active at all. Used by the 10.4 template-version
// check and to source the per-template constraint values (3.4 暫定制約).
func (s *Store) ActiveTemplateVersion(ctx context.Context, templateID string) (int32, bool, error) {
	var v int32
	err := s.pool.QueryRow(ctx,
		`SELECT version FROM action_templates
		  WHERE template_id = $1 AND status = 'active'
		  ORDER BY version DESC LIMIT 1`,
		templateID,
	).Scan(&v)
	if errors.Is(err, pgx.ErrNoRows) {
		return 0, false, nil
	}
	if err != nil {
		return 0, false, fmt.Errorf("store: active template version: %w", err)
	}
	return v, true, nil
}

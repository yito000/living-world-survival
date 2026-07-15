package store

import (
	"context"
	"errors"
	"fmt"

	"github.com/jackc/pgx/v5"
)

// SaveActorState upserts an actor's runtime state with monotonically increasing
// version (3.5 / 付録C). A received version that is <= the stored one is ignored
// (updated=false) so a late/duplicate save never overwrites a newer state. The
// payload is stored as jsonb. Returns updated=true on insert or a forward update.
func (s *Store) SaveActorState(ctx context.Context, actorID, worldID string, version int64, payload []byte) (bool, error) {
	var returnedID string
	err := s.pool.QueryRow(ctx,
		`INSERT INTO actor_runtime_states (actor_id, world_id, version, payload, updated_at)
		 VALUES ($1, $2, $3, $4::jsonb, now())
		 ON CONFLICT (actor_id) DO UPDATE
		    SET world_id = EXCLUDED.world_id,
		        version  = EXCLUDED.version,
		        payload  = EXCLUDED.payload,
		        updated_at = now()
		  WHERE actor_runtime_states.version < EXCLUDED.version
		 RETURNING actor_id::text`,
		actorID, worldID, version, jsonbArg(payload),
	).Scan(&returnedID)
	if errors.Is(err, pgx.ErrNoRows) {
		// ON CONFLICT WHERE excluded a stale/duplicate version: no update.
		return false, nil
	}
	if err != nil {
		return false, fmt.Errorf("store: save actor state: %w", err)
	}
	return true, nil
}

// LoadActorStateWorld returns the world_id currently recorded for an actor, or
// ErrNotFound. Used to resolve world_id when a Save request's payload omits it.
func (s *Store) LoadActorStateWorld(ctx context.Context, actorID string) (string, error) {
	var worldID string
	err := s.pool.QueryRow(ctx,
		`SELECT world_id::text FROM actor_runtime_states WHERE actor_id = $1`, actorID,
	).Scan(&worldID)
	if errors.Is(err, pgx.ErrNoRows) {
		return "", ErrNotFound
	}
	if err != nil {
		return "", fmt.Errorf("store: load actor world: %w", err)
	}
	return worldID, nil
}

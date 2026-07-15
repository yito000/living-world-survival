package store

import (
	"context"
	"errors"
	"fmt"

	"github.com/jackc/pgx/v5"
)

// Bootstrap is the active-snapshot view returned to a Dedicated Server at
// startup (before the event tail is appended). SnapshotID is empty and
// Sequence is 0 for a world that has no active snapshot yet.
type Bootstrap struct {
	SnapshotID string
	Sequence   int64
	Payload    []byte
}

// LoadWorldBootstrap returns the active snapshot for a world. It returns
// ErrNotFound when the world row does not exist. When the world exists but has
// no active snapshot, it returns an empty SnapshotID, Sequence 0 and an empty
// JSON object payload (new-world初期化, 3.2).
func (s *Store) LoadWorldBootstrap(ctx context.Context, worldID string) (Bootstrap, error) {
	var activeSnapshotID *string
	err := s.pool.QueryRow(ctx,
		`SELECT active_snapshot_id FROM worlds WHERE world_id = $1`, worldID,
	).Scan(&activeSnapshotID)
	if errors.Is(err, pgx.ErrNoRows) {
		return Bootstrap{}, ErrNotFound
	}
	if err != nil {
		return Bootstrap{}, fmt.Errorf("store: load world: %w", err)
	}
	if activeSnapshotID == nil {
		return Bootstrap{SnapshotID: "", Sequence: 0, Payload: []byte("{}")}, nil
	}

	var b Bootstrap
	err = s.pool.QueryRow(ctx,
		`SELECT snapshot_id::text, sequence, payload FROM world_snapshots WHERE snapshot_id = $1`,
		*activeSnapshotID,
	).Scan(&b.SnapshotID, &b.Sequence, &b.Payload)
	if errors.Is(err, pgx.ErrNoRows) {
		// active pointer dangling: treat as empty (defensive; should not happen).
		return Bootstrap{SnapshotID: "", Sequence: 0, Payload: []byte("{}")}, nil
	}
	if err != nil {
		return Bootstrap{}, fmt.Errorf("store: load snapshot: %w", err)
	}
	return b, nil
}

// SaveSnapshot persists a snapshot and switches the world's active pointer in a
// single transaction (staging→active, 3.4). The caller MUST have already
// verified the checksum against the payload. When a snapshot for
// (world_id, sequence) already exists it is idempotent: the existing snapshot
// is (re)activated and its id returned. Returns ErrNotFound when the world row
// is absent.
func (s *Store) SaveSnapshot(ctx context.Context, worldID string, sequence int64, checksum string, payload []byte) (string, error) {
	tx, err := s.pool.Begin(ctx)
	if err != nil {
		return "", err
	}
	defer tx.Rollback(ctx) //nolint:errcheck // best-effort on non-committed tx

	// Idempotency: reuse an existing snapshot at the same (world_id, sequence).
	var snapshotID string
	err = tx.QueryRow(ctx,
		`SELECT snapshot_id::text FROM world_snapshots WHERE world_id = $1 AND sequence = $2`,
		worldID, sequence,
	).Scan(&snapshotID)
	switch {
	case err == nil:
		// exists — fall through to (re)activate.
	case errors.Is(err, pgx.ErrNoRows):
		snapshotID = NewUUID()
		if _, err = tx.Exec(ctx,
			`INSERT INTO world_snapshots (snapshot_id, world_id, sequence, payload, checksum)
			 VALUES ($1, $2, $3, $4::jsonb, $5)`,
			snapshotID, worldID, sequence, jsonbArg(payload), checksum,
		); err != nil {
			return "", fmt.Errorf("store: insert snapshot: %w", err)
		}
	default:
		return "", fmt.Errorf("store: lookup snapshot: %w", err)
	}

	// Activate: only now does the snapshot become the world's source of truth.
	tag, err := tx.Exec(ctx,
		`UPDATE worlds SET active_snapshot_id = $2 WHERE world_id = $1`, worldID, snapshotID)
	if err != nil {
		return "", fmt.Errorf("store: activate snapshot: %w", err)
	}
	if tag.RowsAffected() == 0 {
		return "", ErrNotFound
	}
	if err := tx.Commit(ctx); err != nil {
		return "", err
	}
	return snapshotID, nil
}

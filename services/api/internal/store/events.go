package store

import (
	"context"
	"fmt"
	"strings"
	"time"

	"github.com/jackc/pgx/v5"
)

// EventInput is a domain event as produced by the Dedicated Server. event_id
// (ULID) and local_sequence are DS-generated; the world-wide sequence is
// assigned by the API (MVP 13.1). Payload is JSON-encoded bytes.
type EventInput struct {
	EventID       string
	WorldID       string
	AggregateID   string
	LocalSequence int64
	Type          string
	Payload       []byte
	OccurredAtMs  int64
}

// AppendOutcome is the per-event result of AppendEvents.
type AppendOutcome int

const (
	// AppendOK means the event was newly persisted with an API-assigned sequence.
	AppendOK AppendOutcome = iota
	// AppendDuplicate means the event_id was already persisted (idempotent no-op).
	AppendDuplicate
	// AppendConflict means a unique constraint blocked the insert (e.g. a
	// racing sequence). The DS recovers by resending or re-syncing.
	AppendConflict
)

// TailEvent is a persisted domain event returned by LoadEventTail (ascending
// sequence). OccurredAt is timestamptz; the gRPC layer converts it to unix ms.
type TailEvent struct {
	EventID       string
	WorldID       string
	AggregateID   string
	LocalSequence int64
	Type          string
	Payload       []byte
	OccurredAt    time.Time
}

// AppendEvents persists a batch of domain events in one transaction and returns
// a per-event outcome (same order and length as events). Duplicates are ignored
// idempotently; new events get an API-assigned world-wide sequence. Each newly
// persisted event is also enqueued into outbox_messages in the same transaction
// (transactional outbox), so the relay can publish it to NATS (3.3/3.6).
func (s *Store) AppendEvents(ctx context.Context, events []EventInput) ([]AppendOutcome, error) {
	out := make([]AppendOutcome, len(events))
	if len(events) == 0 {
		return out, nil
	}

	tx, err := s.pool.Begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback(ctx) //nolint:errcheck // best-effort on non-committed tx

	for i, e := range events {
		outcome, err := appendOneTx(ctx, tx, e)
		if err != nil {
			return nil, err
		}
		out[i] = outcome
	}

	if err := tx.Commit(ctx); err != nil {
		return nil, err
	}
	return out, nil
}

// appendOneTx processes a single event. Sequence numbering is serialized per
// world by a transaction-scoped advisory lock so concurrent AppendEvents calls
// produce a dense, unique sequence instead of colliding. The INSERT runs inside
// a nested savepoint so a unique violation yields CONFLICT without poisoning the
// surrounding transaction.
func appendOneTx(ctx context.Context, tx pgx.Tx, e EventInput) (AppendOutcome, error) {
	// Serialize sequence assignment for this world (released at tx end).
	if _, err := tx.Exec(ctx, `SELECT pg_advisory_xact_lock(hashtext($1))`, e.WorldID); err != nil {
		return 0, fmt.Errorf("store: advisory lock: %w", err)
	}

	// Idempotent dedup by event_id (PK).
	var exists bool
	if err := tx.QueryRow(ctx,
		`SELECT EXISTS(SELECT 1 FROM domain_events WHERE event_id = $1)`, e.EventID,
	).Scan(&exists); err != nil {
		return 0, fmt.Errorf("store: dedup check: %w", err)
	}
	if exists {
		return AppendDuplicate, nil
	}

	// Assign the next world-wide sequence (sees prior inserts in this tx).
	var next int64
	if err := tx.QueryRow(ctx,
		`SELECT coalesce(max(sequence), 0) + 1 FROM domain_events WHERE world_id = $1`, e.WorldID,
	).Scan(&next); err != nil {
		return 0, fmt.Errorf("store: next sequence: %w", err)
	}

	sp, err := tx.Begin(ctx) // nested savepoint
	if err != nil {
		return 0, err
	}
	_, err = sp.Exec(ctx,
		`INSERT INTO domain_events
		   (event_id, world_id, aggregate_id, local_sequence, sequence, type, payload, occurred_at)
		 VALUES ($1, $2, $3, $4, $5, $6, $7::jsonb, $8)`,
		e.EventID, e.WorldID, e.AggregateID, e.LocalSequence, next, e.Type,
		jsonbArg(e.Payload), msToTime(e.OccurredAtMs),
	)
	if err == nil {
		// Transactional outbox: publish target for WorldState/Batch consumers.
		_, err = sp.Exec(ctx,
			`INSERT INTO outbox_messages (message_id, topic, payload) VALUES ($1, $2, $3::jsonb)`,
			NewUUID(), eventSubject(e.WorldID, e.Type), jsonbArg(e.Payload),
		)
	}
	if err != nil {
		_ = sp.Rollback(ctx)
		if isUniqueViolation(err) {
			return AppendConflict, nil
		}
		return 0, fmt.Errorf("store: insert event: %w", err)
	}
	if err := sp.Commit(ctx); err != nil {
		return 0, err
	}
	return AppendOK, nil
}

// LoadEventTail returns all events for a world with sequence > afterSequence,
// in ascending sequence order (the DS applies them in order after restoring the
// snapshot, 3.2).
func (s *Store) LoadEventTail(ctx context.Context, worldID string, afterSequence int64) ([]TailEvent, error) {
	rows, err := s.pool.Query(ctx,
		`SELECT event_id, world_id::text, aggregate_id::text, local_sequence, type, payload, occurred_at
		   FROM domain_events
		  WHERE world_id = $1 AND sequence > $2
		  ORDER BY sequence ASC`,
		worldID, afterSequence,
	)
	if err != nil {
		return nil, fmt.Errorf("store: load event tail: %w", err)
	}
	defer rows.Close()

	var tail []TailEvent
	for rows.Next() {
		var t TailEvent
		if err := rows.Scan(&t.EventID, &t.WorldID, &t.AggregateID, &t.LocalSequence, &t.Type, &t.Payload, &t.OccurredAt); err != nil {
			return nil, fmt.Errorf("store: scan event: %w", err)
		}
		tail = append(tail, t)
	}
	if err := rows.Err(); err != nil {
		return nil, err
	}
	return tail, nil
}

// eventSubject derives the NATS subject for a domain event from its world and
// type (MVP 14.3: world.{id}.event.{category}). Category is inferred from the
// event type; unknown types default to actor.
func eventSubject(worldID, eventType string) string {
	category := "actor"
	lt := strings.ToLower(eventType)
	switch {
	case strings.Contains(lt, "resource"):
		category = "resource"
	case strings.Contains(lt, "economy"), strings.Contains(lt, "purchase"), strings.Contains(lt, "currency"):
		category = "economy"
	}
	return fmt.Sprintf("world.%s.event.%s", worldID, category)
}

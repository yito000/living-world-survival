package store

import (
	"context"
	"fmt"
)

// OutboxMessage is an unpublished row awaiting relay to NATS (3.6).
type OutboxMessage struct {
	MessageID string
	Topic     string
	Payload   []byte
}

// FetchUnpublished returns up to limit unpublished outbox rows, oldest first,
// using the partial index outbox_unpublished_idx (published_at IS NULL).
func (s *Store) FetchUnpublished(ctx context.Context, limit int) ([]OutboxMessage, error) {
	rows, err := s.pool.Query(ctx,
		`SELECT message_id::text, topic, payload
		   FROM outbox_messages
		  WHERE published_at IS NULL
		  ORDER BY available_at ASC
		  LIMIT $1`,
		limit,
	)
	if err != nil {
		return nil, fmt.Errorf("store: fetch unpublished: %w", err)
	}
	defer rows.Close()

	var msgs []OutboxMessage
	for rows.Next() {
		var m OutboxMessage
		if err := rows.Scan(&m.MessageID, &m.Topic, &m.Payload); err != nil {
			return nil, fmt.Errorf("store: scan outbox: %w", err)
		}
		msgs = append(msgs, m)
	}
	return msgs, rows.Err()
}

// MarkPublished stamps published_at=now() after a successful NATS publish.
func (s *Store) MarkPublished(ctx context.Context, messageID string) error {
	_, err := s.pool.Exec(ctx,
		`UPDATE outbox_messages SET published_at = now() WHERE message_id = $1`, messageID)
	if err != nil {
		return fmt.Errorf("store: mark published: %w", err)
	}
	return nil
}

// IncrementRetry bumps retry_count for a row whose publish failed.
func (s *Store) IncrementRetry(ctx context.Context, messageID string) error {
	_, err := s.pool.Exec(ctx,
		`UPDATE outbox_messages SET retry_count = retry_count + 1 WHERE message_id = $1`, messageID)
	if err != nil {
		return fmt.Errorf("store: increment retry: %w", err)
	}
	return nil
}

// EnqueueOutbox inserts a message for the relay to publish (used by tests and
// any API-side producer outside the domain-event path).
func (s *Store) EnqueueOutbox(ctx context.Context, topic string, payload []byte) (string, error) {
	id := NewUUID()
	_, err := s.pool.Exec(ctx,
		`INSERT INTO outbox_messages (message_id, topic, payload) VALUES ($1, $2, $3::jsonb)`,
		id, topic, jsonbArg(payload),
	)
	if err != nil {
		return "", fmt.Errorf("store: enqueue outbox: %w", err)
	}
	return id, nil
}

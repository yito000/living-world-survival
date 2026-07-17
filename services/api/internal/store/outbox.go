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

// OutboxBacklog is the relay's queue health at one instant (10B 3.2).
//
// float64 は「経過秒数」であって通貨・数量ではない（MVP 13.1 の禁止対象外）。
//
//nolint:forbidigo // Elapsed seconds for metrics, not money.
type OutboxBacklog struct {
	Depth        int     // published_at IS NULL の件数
	OldestAgeSec float64 // 最古の未 publish 行の滞留秒数（Depth=0 なら 0）
}

// OutboxBacklogStats reports the unpublished depth and the age of the oldest
// unpublished row. Soak (10B 3.2) watches both: depth alone cannot separate
// "busy but draining" from "stuck".
func (s *Store) OutboxBacklogStats(ctx context.Context) (OutboxBacklog, error) {
	var b OutboxBacklog
	err := s.pool.QueryRow(ctx,
		`SELECT count(*),
		        COALESCE(EXTRACT(EPOCH FROM (now() - min(available_at))), 0)
		   FROM outbox_messages
		  WHERE published_at IS NULL`,
	).Scan(&b.Depth, &b.OldestAgeSec)
	if err != nil {
		return OutboxBacklog{}, fmt.Errorf("store: outbox backlog: %w", err)
	}
	// クロックスキューで負値が出ても、そのまま Gauge に載せない。
	if b.OldestAgeSec < 0 {
		b.OldestAgeSec = 0
	}
	return b, nil
}

// NewestEventAgeSeconds returns how many seconds ago the newest domain_events
// row occurred (第13章 イベント Lag). ok=false when there are no events yet.
//
//nolint:forbidigo // Elapsed seconds for metrics, not money.
func (s *Store) NewestEventAgeSeconds(ctx context.Context) (float64, bool, error) {
	var age *float64
	err := s.pool.QueryRow(ctx,
		`SELECT EXTRACT(EPOCH FROM (now() - max(occurred_at))) FROM domain_events`,
	).Scan(&age)
	if err != nil {
		return 0, false, fmt.Errorf("store: newest event age: %w", err)
	}
	if age == nil {
		return 0, false, nil
	}
	if *age < 0 {
		return 0, true, nil
	}
	return *age, true, nil
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

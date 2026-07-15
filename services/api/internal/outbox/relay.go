// Package outbox implements the API's transactional-outbox relay: it polls
// unpublished outbox_messages at ≤1s intervals and publishes them to NATS
// JetStream, stamping published_at only after a successful publish (3.6).
// Delivery is at-least-once; consumers deduplicate via inbox_dedup.
package outbox

import (
	"context"
	"log"
	"time"

	"living-world-survival/services/api/internal/store"
)

// Publisher publishes a message to a subject. Implementations must return a
// non-nil error unless the broker acknowledged the message (so an unacked
// message is retried rather than lost).
type Publisher interface {
	Publish(ctx context.Context, subject string, data []byte) error
}

// Relay drains outbox_messages to a Publisher on a fixed interval.
type Relay struct {
	Store     *store.Store
	Publisher Publisher
	Interval  time.Duration
	Batch     int
}

// NewRelay builds a Relay with sane defaults (≤1s interval, batch 100).
func NewRelay(st *store.Store, pub Publisher, interval time.Duration, batch int) *Relay {
	if interval <= 0 || interval > time.Second {
		interval = time.Second
	}
	if batch <= 0 {
		batch = 100
	}
	return &Relay{Store: st, Publisher: pub, Interval: interval, Batch: batch}
}

// Run polls until ctx is cancelled. It runs one drain immediately, then every
// Interval.
func (r *Relay) Run(ctx context.Context) {
	ticker := time.NewTicker(r.Interval)
	defer ticker.Stop()

	if n, err := r.Drain(ctx); err != nil {
		log.Printf("outbox: drain error: %v", err)
	} else if n > 0 {
		log.Printf("outbox: published %d message(s)", n)
	}

	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			if n, err := r.Drain(ctx); err != nil {
				log.Printf("outbox: drain error: %v", err)
			} else if n > 0 {
				log.Printf("outbox: published %d message(s)", n)
			}
		}
	}
}

// Drain publishes one batch of unpublished messages and returns how many were
// successfully published. A publish failure increments retry_count and leaves
// published_at NULL so the row is retried on the next tick.
func (r *Relay) Drain(ctx context.Context) (int, error) {
	msgs, err := r.Store.FetchUnpublished(ctx, r.Batch)
	if err != nil {
		return 0, err
	}
	published := 0
	for _, m := range msgs {
		if err := r.Publisher.Publish(ctx, m.Topic, m.Payload); err != nil {
			log.Printf("outbox: publish %s to %s failed: %v", m.MessageID, m.Topic, err)
			if rerr := r.Store.IncrementRetry(ctx, m.MessageID); rerr != nil {
				log.Printf("outbox: increment retry %s: %v", m.MessageID, rerr)
			}
			continue
		}
		if err := r.Store.MarkPublished(ctx, m.MessageID); err != nil {
			// Published to NATS but failed to record it: will be re-published
			// (at-least-once). Consumers dedup via inbox_dedup.
			log.Printf("outbox: mark published %s: %v", m.MessageID, err)
			continue
		}
		published++
	}
	return published, nil
}

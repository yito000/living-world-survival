// Package outbox implements the API's transactional-outbox relay: it polls
// unpublished outbox_messages at ≤1s intervals and publishes them to NATS
// JetStream, stamping published_at only after a successful publish (3.6).
// Delivery is at-least-once; consumers deduplicate via inbox_dedup.
package outbox

import (
	"context"
	"time"

	"living-world-survival/services/api/internal/metrics"
	"living-world-survival/services/api/internal/store"
	"living-world-survival/services/common/obs"
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

	r.drainOnce(ctx)
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			r.drainOnce(ctx)
		}
	}
}

func (r *Relay) drainOnce(ctx context.Context) {
	if n, err := r.Drain(ctx); err != nil {
		obs.L(ctx).Warn("outbox drain error", "error", err.Error())
	} else if n > 0 {
		obs.L(ctx).Info("outbox published", "count", n)
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
			metrics.OutboxPublished.WithLabelValues("publish_failed").Inc()
			obs.L(ctx).Warn("outbox publish failed",
				"message_id", m.MessageID, "topic", m.Topic, "error", err.Error())
			if rerr := r.Store.IncrementRetry(ctx, m.MessageID); rerr != nil {
				obs.L(ctx).Warn("outbox increment retry failed",
					"message_id", m.MessageID, "error", rerr.Error())
			}
			continue
		}
		if err := r.Store.MarkPublished(ctx, m.MessageID); err != nil {
			// Published to NATS but failed to record it: will be re-published
			// (at-least-once). Consumers dedup via inbox_dedup.
			metrics.OutboxPublished.WithLabelValues("mark_failed").Inc()
			obs.L(ctx).Warn("outbox mark published failed",
				"message_id", m.MessageID, "error", err.Error())
			continue
		}
		metrics.OutboxPublished.WithLabelValues("ok").Inc()
		published++
	}
	return published, nil
}

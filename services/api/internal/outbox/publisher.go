package outbox

import (
	"context"
	"fmt"

	"github.com/nats-io/nats.go"
)

// worldEventsStream is the JetStream stream capturing all world event subjects
// (MVP 14.3: world.{id}.event.*). It is created on demand if absent.
const (
	worldEventsStream  = "WORLD_EVENTS"
	worldEventsSubject = "world.>"
)

// JetStreamPublisher publishes to NATS JetStream, ensuring the stream exists.
type JetStreamPublisher struct {
	js nats.JetStreamContext
}

// NewJetStreamPublisher wraps a NATS connection's JetStream context and ensures
// the WORLD_EVENTS stream exists so publishes are persisted (at-least-once).
func NewJetStreamPublisher(nc *nats.Conn) (*JetStreamPublisher, error) {
	js, err := nc.JetStream()
	if err != nil {
		return nil, fmt.Errorf("outbox: jetstream context: %w", err)
	}
	if _, err := js.StreamInfo(worldEventsStream); err != nil {
		if _, aerr := js.AddStream(&nats.StreamConfig{
			Name:     worldEventsStream,
			Subjects: []string{worldEventsSubject},
			Storage:  nats.FileStorage,
		}); aerr != nil {
			return nil, fmt.Errorf("outbox: ensure stream: %w", aerr)
		}
	}
	return &JetStreamPublisher{js: js}, nil
}

// Publish sends data to subject and waits for the JetStream ack.
func (p *JetStreamPublisher) Publish(_ context.Context, subject string, data []byte) error {
	if _, err := p.js.Publish(subject, data); err != nil {
		return fmt.Errorf("outbox: jetstream publish: %w", err)
	}
	return nil
}

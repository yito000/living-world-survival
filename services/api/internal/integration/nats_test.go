package integration

import (
	"testing"
	"time"

	"github.com/nats-io/nats.go"

	"living-world-survival/services/api/internal/outbox"
)

// connectJetStream returns a JetStream-backed Publisher, self-skipping the test
// when NATS/JetStream is not reachable so `go test ./...` stays green without infra.
func connectJetStream(t *testing.T, url string) outbox.Publisher {
	t.Helper()
	nc, err := nats.Connect(url, nats.Timeout(2*time.Second))
	if err != nil {
		t.Skipf("no NATS at %s: %v", url, err)
	}
	t.Cleanup(nc.Close)
	pub, err := outbox.NewJetStreamPublisher(nc)
	if err != nil {
		t.Skipf("JetStream unavailable at %s: %v", url, err)
	}
	return pub
}

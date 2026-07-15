// Package config loads the M2 API service configuration from the environment.
// HTTP (health) and gRPC (WorldData/ActorState) listen on separate ports so a
// single process can serve both (3.1).
package config

import (
	"os"
	"strings"
	"time"

	"github.com/nats-io/nats.go"
)

// Config holds the resolved runtime configuration.
type Config struct {
	HTTPAddr    string // HTTP health listen address, e.g. ":8082"
	GRPCAddr    string // internal gRPC listen address, e.g. ":8092"
	DatabaseURL string
	NATSURL     string

	// Optional shared secret for internal gRPC (x-service-secret metadata). Empty
	// means no gRPC auth is enforced (dev; internal-only network, MVP-SEC-007).
	GRPCSharedSecret string

	// Path to the Item Definition master JSON seeded at startup (3.8).
	ItemDefPath string

	// OutboxInterval is how often the relay polls unpublished outbox rows
	// (must be ≤1s to meet the publish latency budget, 3.6).
	OutboxInterval time.Duration
}

// Load reads the environment and returns a Config with dev-safe defaults.
func Load() *Config {
	return &Config{
		HTTPAddr:         ":" + envOr("API_PORT", "8082"),
		GRPCAddr:         ":" + envOr("API_GRPC_PORT", "8092"),
		DatabaseURL:      envOr("DATABASE_URL", "postgres://survival:survival@localhost:5432/survival?sslmode=disable"),
		NATSURL:          envOr("NATS_URL", nats.DefaultURL),
		GRPCSharedSecret: strings.TrimSpace(os.Getenv("API_GRPC_SHARED_SECRET")),
		ItemDefPath:      envOr("ITEM_DEFINITIONS_PATH", "data/item_definitions.json"),
		OutboxInterval:   durationOr("OUTBOX_RELAY_INTERVAL", time.Second),
	}
}

func envOr(key, fallback string) string {
	if v := strings.TrimSpace(os.Getenv(key)); v != "" {
		return v
	}
	return fallback
}

func durationOr(key string, fallback time.Duration) time.Duration {
	v := strings.TrimSpace(os.Getenv(key))
	if v == "" {
		return fallback
	}
	d, err := time.ParseDuration(v)
	if err != nil || d <= 0 {
		return fallback
	}
	return d
}

// Package config loads and validates the M1 Auth service configuration from
// the environment (3.7). Secrets (JWT signing key, Join Ticket Ed25519 keys)
// are provided via env / .env and never committed as real values.
package config

import (
	"crypto/ed25519"
	"encoding/base64"
	"fmt"
	"os"
	"strings"
	"time"
)

// Config holds the resolved runtime configuration.
type Config struct {
	HTTPAddr    string // REST listen address, e.g. ":8081"
	GRPCAddr    string // Internal gRPC listen address, e.g. ":9091"
	DatabaseURL string

	// Access token (short-lived JWT, HS256).
	JWTSigningKey   []byte
	AccessTokenTTL  time.Duration
	RefreshTokenTTL time.Duration

	// Join Ticket signing (Ed25519 / EdDSA JWS). The private key signs; the
	// public key is distributed to the Dedicated Server (04A) for pre-verify.
	JoinTicketPrivateKey ed25519.PrivateKey
	JoinTicketPublicKey  ed25519.PublicKey
	JoinTicketTTL        time.Duration

	// Optional shared secret for internal gRPC (future service credentials,
	// BSD第11章). Empty means no gRPC auth is enforced (M1 internal-only net).
	GRPCSharedSecret string
}

// Load reads the environment and returns a validated Config. It returns an
// error when a required secret is missing or malformed so that authd fails
// fast on startup rather than serving with an insecure/zero key.
func Load() (*Config, error) {
	c := &Config{
		HTTPAddr:         ":" + envOr("AUTH_PORT", "8081"),
		GRPCAddr:         ":" + envOr("AUTH_GRPC_PORT", "9091"),
		DatabaseURL:      envOr("DATABASE_URL", "postgres://survival:survival@localhost:5432/survival?sslmode=disable"),
		GRPCSharedSecret: strings.TrimSpace(os.Getenv("AUTH_GRPC_SHARED_SECRET")),
	}

	key := strings.TrimSpace(os.Getenv("JWT_SIGNING_KEY"))
	if key == "" {
		return nil, fmt.Errorf("config: JWT_SIGNING_KEY is required")
	}
	c.JWTSigningKey = []byte(key)

	var err error
	if c.AccessTokenTTL, err = durationOr("ACCESS_TOKEN_TTL", 15*time.Minute); err != nil {
		return nil, err
	}
	if c.RefreshTokenTTL, err = durationOr("REFRESH_TOKEN_TTL", 720*time.Hour); err != nil {
		return nil, err
	}
	if c.JoinTicketTTL, err = durationOr("JOIN_TICKET_TTL", 60*time.Second); err != nil {
		return nil, err
	}

	if c.JoinTicketPrivateKey, err = loadEd25519Private("JOIN_TICKET_SIGNING_KEY"); err != nil {
		return nil, err
	}
	c.JoinTicketPublicKey = c.JoinTicketPrivateKey.Public().(ed25519.PublicKey)

	// If a public key is supplied, cross-check it matches the private key so a
	// mismatched dev config (which the DS would reject) is caught at startup.
	if pubB64 := strings.TrimSpace(os.Getenv("JOIN_TICKET_PUBLIC_KEY")); pubB64 != "" {
		pub, derr := base64.StdEncoding.DecodeString(pubB64)
		if derr != nil {
			return nil, fmt.Errorf("config: JOIN_TICKET_PUBLIC_KEY not valid base64: %w", derr)
		}
		if !ed25519.PublicKey(pub).Equal(c.JoinTicketPublicKey) {
			return nil, fmt.Errorf("config: JOIN_TICKET_PUBLIC_KEY does not match JOIN_TICKET_SIGNING_KEY")
		}
	}

	return c, nil
}

// loadEd25519Private decodes a base64 (std) Ed25519 seed (32 bytes) or full
// private key (64 bytes) from the named env var.
func loadEd25519Private(name string) (ed25519.PrivateKey, error) {
	raw := strings.TrimSpace(os.Getenv(name))
	if raw == "" {
		return nil, fmt.Errorf("config: %s is required (base64 Ed25519 seed)", name)
	}
	b, err := base64.StdEncoding.DecodeString(raw)
	if err != nil {
		return nil, fmt.Errorf("config: %s not valid base64: %w", name, err)
	}
	switch len(b) {
	case ed25519.SeedSize: // 32
		return ed25519.NewKeyFromSeed(b), nil
	case ed25519.PrivateKeySize: // 64
		return ed25519.PrivateKey(b), nil
	default:
		return nil, fmt.Errorf("config: %s must decode to %d or %d bytes, got %d",
			name, ed25519.SeedSize, ed25519.PrivateKeySize, len(b))
	}
}

func durationOr(name string, fallback time.Duration) (time.Duration, error) {
	v := strings.TrimSpace(os.Getenv(name))
	if v == "" {
		return fallback, nil
	}
	d, err := time.ParseDuration(v)
	if err != nil {
		return 0, fmt.Errorf("config: %s invalid duration %q: %w", name, v, err)
	}
	return d, nil
}

func envOr(key, fallback string) string {
	if v := strings.TrimSpace(os.Getenv(key)); v != "" {
		return v
	}
	return fallback
}

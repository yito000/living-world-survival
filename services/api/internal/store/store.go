// Package store is the pgx-backed persistence layer for the API service, which
// is the single authoritative Writer of world snapshots, domain events, actor
// runtime states and inventories (MVP 12.2.1 / 付録C). All UUIDs are handled as
// strings and cast by Postgres; JSON payloads are stored as jsonb via ::jsonb.
package store

import (
	"crypto/rand"
	"errors"
	"fmt"
	"time"

	"github.com/jackc/pgx/v5/pgconn"
	"github.com/jackc/pgx/v5/pgxpool"
)

// Sentinel errors surfaced to the gRPC layer for status mapping.
var (
	// ErrNotFound is returned when a referenced world/aggregate does not exist.
	ErrNotFound = errors.New("store: not found")
)

// Store wraps a pgx pool.
type Store struct {
	pool *pgxpool.Pool
}

// New returns a Store over the given pool.
func New(pool *pgxpool.Pool) *Store { return &Store{pool: pool} }

// Pool exposes the underlying pool (used by tests / health checks).
func (s *Store) Pool() *pgxpool.Pool { return s.pool }

// NewUUID returns a random RFC-4122 v4 UUID string.
func NewUUID() string {
	var b [16]byte
	if _, err := rand.Read(b[:]); err != nil {
		panic(fmt.Sprintf("store: rand: %v", err))
	}
	b[6] = (b[6] & 0x0f) | 0x40 // version 4
	b[8] = (b[8] & 0x3f) | 0x80 // variant 10
	return fmt.Sprintf("%x-%x-%x-%x-%x", b[0:4], b[4:6], b[6:8], b[8:10], b[10:16])
}

// isUniqueViolation reports whether err is a Postgres unique_violation (23505).
func isUniqueViolation(err error) bool {
	var pgErr *pgconn.PgError
	return errors.As(err, &pgErr) && pgErr.Code == "23505"
}

// jsonbArg returns a non-empty JSON text suitable for a ::jsonb cast. Empty or
// nil payloads become an empty JSON object so NOT NULL jsonb columns are safe.
func jsonbArg(payload []byte) string {
	if len(payload) == 0 {
		return "{}"
	}
	return string(payload)
}

// msToTime converts a unix-millis timestamp to a UTC time. Non-positive values
// fall back to the current time so occurred_at is never the epoch by accident.
func msToTime(ms int64) time.Time {
	if ms <= 0 {
		return time.Now().UTC()
	}
	return time.UnixMilli(ms).UTC()
}

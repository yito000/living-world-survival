// Package store is the pgx-backed persistence layer for Auth (accounts,
// credentials, refresh tokens, game servers, join tickets). All UUIDs are
// handled as strings and cast by Postgres; time is stored as timestamptz.
package store

import (
	"context"
	"crypto/rand"
	"errors"
	"fmt"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"
	"github.com/jackc/pgx/v5/pgxpool"
)

// Sentinel errors surfaced to the REST/gRPC layers for status mapping.
var (
	ErrEmailExists    = errors.New("store: email already exists")
	ErrNotFound       = errors.New("store: not found")
	ErrNoServer       = errors.New("store: no ready server")
	ErrTicketNotFound = errors.New("store: join ticket not found")
	ErrTicketUsed     = errors.New("store: join ticket already used")
	ErrTicketExpired  = errors.New("store: join ticket expired")
	ErrServerMismatch = errors.New("store: join ticket server mismatch")
)

// ServerStaleAfter bounds how long since last heartbeat a server stays
// matchmaking-eligible.
const ServerStaleAfter = 60 * time.Second

// Store wraps a pgx pool.
type Store struct {
	pool *pgxpool.Pool
}

// New returns a Store over the given pool.
func New(pool *pgxpool.Pool) *Store { return &Store{pool: pool} }

// --- accounts / credentials -------------------------------------------------

// CreateAccount inserts an account + Argon2id credential atomically and returns
// the new account_id. Returns ErrEmailExists on a unique-violation.
func (s *Store) CreateAccount(ctx context.Context, email, passwordHash, displayName string) (string, error) {
	accountID := NewUUID()
	tx, err := s.pool.Begin(ctx)
	if err != nil {
		return "", err
	}
	defer tx.Rollback(ctx) //nolint:errcheck // best-effort on non-committed tx

	_, err = tx.Exec(ctx,
		`INSERT INTO accounts (account_id, email, display_name) VALUES ($1, $2, $3)`,
		accountID, email, nullIfEmpty(displayName))
	if err != nil {
		if isUniqueViolation(err) {
			return "", ErrEmailExists
		}
		return "", fmt.Errorf("store: insert account: %w", err)
	}
	_, err = tx.Exec(ctx,
		`INSERT INTO password_credentials (account_id, password_hash) VALUES ($1, $2)`,
		accountID, passwordHash)
	if err != nil {
		return "", fmt.Errorf("store: insert credential: %w", err)
	}
	if err := tx.Commit(ctx); err != nil {
		return "", err
	}
	return accountID, nil
}

// Credential is an account's login material.
type Credential struct {
	AccountID    string
	PasswordHash string
}

// GetCredentialByEmail returns the credential for login. ErrNotFound if absent.
func (s *Store) GetCredentialByEmail(ctx context.Context, email string) (*Credential, error) {
	var c Credential
	err := s.pool.QueryRow(ctx,
		`SELECT a.account_id, pc.password_hash
		   FROM accounts a
		   JOIN password_credentials pc ON pc.account_id = a.account_id
		  WHERE a.email = $1`, email).Scan(&c.AccountID, &c.PasswordHash)
	if errors.Is(err, pgx.ErrNoRows) {
		return nil, ErrNotFound
	}
	if err != nil {
		return nil, err
	}
	return &c, nil
}

// --- refresh tokens ---------------------------------------------------------

// RefreshToken is a persisted refresh-token row (hash only, never plaintext).
type RefreshToken struct {
	TokenID   string
	AccountID string
	TokenHash string
	FamilyID  string
	ExpiresAt time.Time
	RevokedAt *time.Time
}

// InsertRefreshToken persists a new refresh token (hash) row.
func (s *Store) InsertRefreshToken(ctx context.Context, rt RefreshToken) error {
	_, err := s.pool.Exec(ctx,
		`INSERT INTO refresh_tokens (token_id, account_id, token_hash, family_id, expires_at)
		 VALUES ($1, $2, $3, $4, $5)`,
		rt.TokenID, rt.AccountID, rt.TokenHash, rt.FamilyID, rt.ExpiresAt)
	return err
}

// GetRefreshTokenByHash looks up a refresh token by its stored hash.
func (s *Store) GetRefreshTokenByHash(ctx context.Context, hash string) (*RefreshToken, error) {
	var rt RefreshToken
	err := s.pool.QueryRow(ctx,
		`SELECT token_id, account_id, token_hash, family_id, expires_at, revoked_at
		   FROM refresh_tokens WHERE token_hash = $1`, hash).
		Scan(&rt.TokenID, &rt.AccountID, &rt.TokenHash, &rt.FamilyID, &rt.ExpiresAt, &rt.RevokedAt)
	if errors.Is(err, pgx.ErrNoRows) {
		return nil, ErrNotFound
	}
	if err != nil {
		return nil, err
	}
	return &rt, nil
}

// RotateRefreshToken revokes the presented token and inserts its successor in
// the same family, atomically. It returns the number of rows revoked so the
// caller can detect a race (should always be 1).
func (s *Store) RotateRefreshToken(ctx context.Context, oldTokenID string, next RefreshToken) error {
	tx, err := s.pool.Begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback(ctx) //nolint:errcheck

	ct, err := tx.Exec(ctx,
		`UPDATE refresh_tokens SET revoked_at = now()
		  WHERE token_id = $1 AND revoked_at IS NULL`, oldTokenID)
	if err != nil {
		return err
	}
	if ct.RowsAffected() != 1 {
		// Someone else already rotated/revoked this token — treat as reuse.
		return ErrNotFound
	}
	_, err = tx.Exec(ctx,
		`INSERT INTO refresh_tokens (token_id, account_id, token_hash, family_id, expires_at)
		 VALUES ($1, $2, $3, $4, $5)`,
		next.TokenID, next.AccountID, next.TokenHash, next.FamilyID, next.ExpiresAt)
	if err != nil {
		return err
	}
	return tx.Commit(ctx)
}

// RevokeFamily revokes every not-yet-revoked token in a family (reuse detection
// and logout). Returns the number of rows revoked.
func (s *Store) RevokeFamily(ctx context.Context, familyID string) (int64, error) {
	ct, err := s.pool.Exec(ctx,
		`UPDATE refresh_tokens SET revoked_at = now()
		  WHERE family_id = $1 AND revoked_at IS NULL`, familyID)
	if err != nil {
		return 0, err
	}
	return ct.RowsAffected(), nil
}

// --- game servers -----------------------------------------------------------

// GameServer is a registered Dedicated Server.
type GameServer struct {
	ServerID string
	WorldID  string
	BuildID  string
	Endpoint string
	Capacity int32
	Ready    bool
}

// UpsertServer registers or updates a Dedicated Server (ready defaults false).
func (s *Store) UpsertServer(ctx context.Context, gs GameServer) error {
	_, err := s.pool.Exec(ctx,
		`INSERT INTO game_servers (server_id, world_id, build_id, endpoint, capacity, ready, status, last_seen)
		 VALUES ($1, $2, $3, $4, $5, false, 'active', now())
		 ON CONFLICT (server_id) DO UPDATE
		    SET world_id = EXCLUDED.world_id,
		        build_id = EXCLUDED.build_id,
		        endpoint = EXCLUDED.endpoint,
		        capacity = EXCLUDED.capacity,
		        status   = 'active',
		        last_seen = now()`,
		gs.ServerID, gs.WorldID, gs.BuildID, gs.Endpoint, gs.Capacity)
	return err
}

// Heartbeat refreshes last_seen and ready flag. Returns false if unregistered.
func (s *Store) Heartbeat(ctx context.Context, serverID string, ready bool) (bool, error) {
	ct, err := s.pool.Exec(ctx,
		`UPDATE game_servers SET last_seen = now(), ready = $2 WHERE server_id = $1`,
		serverID, ready)
	if err != nil {
		return false, err
	}
	return ct.RowsAffected() == 1, nil
}

// MarkDraining takes a server out of matchmaking (ready=false, status=draining).
func (s *Store) MarkDraining(ctx context.Context, serverID string) (bool, error) {
	ct, err := s.pool.Exec(ctx,
		`UPDATE game_servers SET ready = false, status = 'draining' WHERE server_id = $1`,
		serverID)
	if err != nil {
		return false, err
	}
	return ct.RowsAffected() == 1, nil
}

// SelectReadyServer picks a matchmaking-eligible server for the build. It is a
// single query so it can be extended for capacity/load ordering later.
func (s *Store) SelectReadyServer(ctx context.Context, buildID string) (*GameServer, error) {
	var gs GameServer
	err := s.pool.QueryRow(ctx,
		`SELECT server_id, world_id, build_id, endpoint, capacity, ready
		   FROM game_servers
		  WHERE ready = true
		    AND status = 'active'
		    AND build_id = $1
		    AND last_seen > now() - $2::interval
		  ORDER BY last_seen DESC
		  LIMIT 1`,
		buildID, fmt.Sprintf("%d seconds", int(ServerStaleAfter.Seconds()))).
		Scan(&gs.ServerID, &gs.WorldID, &gs.BuildID, &gs.Endpoint, &gs.Capacity, &gs.Ready)
	if errors.Is(err, pgx.ErrNoRows) {
		return nil, ErrNoServer
	}
	if err != nil {
		return nil, err
	}
	return &gs, nil
}

// --- join tickets -----------------------------------------------------------

// JoinTicket is a persisted matchmaking ticket mirroring JoinTicketClaims.
type JoinTicket struct {
	TicketID    string
	AccountID   string
	CharacterID string
	ServerID    string
	WorldID     string
	BuildID     string
	Nonce       string
	IssuedAt    time.Time
	ExpiresAt   time.Time
}

// InsertJoinTicket creates the ticket row (used_at NULL) for later redemption.
func (s *Store) InsertJoinTicket(ctx context.Context, jt JoinTicket) error {
	_, err := s.pool.Exec(ctx,
		`INSERT INTO join_tickets
		    (ticket_id, account_id, character_id, server_id, world_id, build_id, nonce, issued_at, expires_at)
		 VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)`,
		jt.TicketID, jt.AccountID, jt.CharacterID, jt.ServerID,
		jt.WorldID, jt.BuildID, jt.Nonce, jt.IssuedAt, jt.ExpiresAt)
	return err
}

// RedeemJoinTicket atomically consumes a ticket for the given server. The
// single UPDATE ... WHERE used_at IS NULL AND expires_at > now() is the crux of
// single-use (3.4): a concurrent second redeem affects 0 rows. On 0 rows a
// read-only follow-up classifies the failure for a precise error.
func (s *Store) RedeemJoinTicket(ctx context.Context, ticketID, serverID string) (*JoinTicket, error) {
	var jt JoinTicket
	err := s.pool.QueryRow(ctx,
		`UPDATE join_tickets
		    SET used_at = now()
		  WHERE ticket_id = $1
		    AND server_id = $2
		    AND used_at IS NULL
		    AND expires_at > now()
		  RETURNING ticket_id, account_id, character_id, server_id,
		            world_id, build_id, nonce, issued_at, expires_at`,
		ticketID, serverID).
		Scan(&jt.TicketID, &jt.AccountID, &jt.CharacterID, &jt.ServerID,
			&jt.WorldID, &jt.BuildID, &jt.Nonce, &jt.IssuedAt, &jt.ExpiresAt)
	if err == nil {
		return &jt, nil
	}
	if !errors.Is(err, pgx.ErrNoRows) {
		return nil, err
	}
	return nil, s.classifyRedeemFailure(ctx, ticketID, serverID)
}

// classifyRedeemFailure inspects why the atomic redeem matched no row.
func (s *Store) classifyRedeemFailure(ctx context.Context, ticketID, serverID string) error {
	var (
		rowServer string
		usedAt    *time.Time
		expiresAt time.Time
	)
	err := s.pool.QueryRow(ctx,
		`SELECT server_id, used_at, expires_at FROM join_tickets WHERE ticket_id = $1`,
		ticketID).Scan(&rowServer, &usedAt, &expiresAt)
	if errors.Is(err, pgx.ErrNoRows) {
		return ErrTicketNotFound
	}
	if err != nil {
		return err
	}
	if rowServer != serverID {
		return ErrServerMismatch
	}
	if usedAt != nil {
		return ErrTicketUsed
	}
	if !expiresAt.After(time.Now()) {
		return ErrTicketExpired
	}
	// Row exists, unused, unexpired, server matches — should have redeemed;
	// surface a generic not-found to avoid leaking ambiguous state.
	return ErrTicketNotFound
}

// --- helpers ----------------------------------------------------------------

func nullIfEmpty(s string) any {
	if s == "" {
		return nil
	}
	return s
}

func isUniqueViolation(err error) bool {
	var pgErr *pgconn.PgError
	return errors.As(err, &pgErr) && pgErr.Code == "23505"
}

// NewUUID returns a random RFC 4122 v4 UUID string.
func NewUUID() string {
	var b [16]byte
	if _, err := rand.Read(b[:]); err != nil {
		panic(fmt.Sprintf("store: uuid rand: %v", err))
	}
	b[6] = (b[6] & 0x0f) | 0x40 // version 4
	b[8] = (b[8] & 0x3f) | 0x80 // variant 10
	return fmt.Sprintf("%x-%x-%x-%x-%x", b[0:4], b[4:6], b[6:8], b[8:10], b[10:16])
}

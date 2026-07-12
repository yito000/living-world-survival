// Package token issues and verifies access tokens (short-lived HS256 JWT) and
// manages opaque refresh tokens with rotation + reuse detection (3.3, RFC 9700).
// Refresh tokens are only ever persisted as SHA-256 hashes.
package token

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"errors"
	"fmt"
	"time"

	"github.com/golang-jwt/jwt/v5"

	"living-world-survival/services/auth/internal/store"
)

const accessAudience = "survival-client"

// Errors surfaced to the REST layer (all map to 401 for the client).
var (
	ErrInvalidRefresh = errors.New("token: invalid or expired refresh token")
	ErrReuseDetected  = errors.New("token: refresh token reuse detected")
	ErrInvalidAccess  = errors.New("token: invalid access token")
)

// RefreshStore is the persistence surface the token service needs. store.Store
// satisfies it; tests provide a fake.
type RefreshStore interface {
	InsertRefreshToken(ctx context.Context, rt store.RefreshToken) error
	GetRefreshTokenByHash(ctx context.Context, hash string) (*store.RefreshToken, error)
	RotateRefreshToken(ctx context.Context, oldTokenID string, next store.RefreshToken) error
	RevokeFamily(ctx context.Context, familyID string) (int64, error)
}

// AccessClaims are the JWT claims of an access token.
type AccessClaims struct {
	FamilyID string `json:"fam"`
	jwt.RegisteredClaims
}

// Pair is a freshly issued access+refresh pair returned to the client.
type Pair struct {
	AccessToken  string
	RefreshToken string // opaque plaintext, shown to client once
	ExpiresIn    int64  // access token lifetime in seconds
	FamilyID     string
}

// Service issues/verifies tokens against a RefreshStore.
type Service struct {
	signingKey []byte
	accessTTL  time.Duration
	refreshTTL time.Duration
	rs         RefreshStore
	now        func() time.Time
}

// NewService constructs a token Service.
func NewService(signingKey []byte, accessTTL, refreshTTL time.Duration, rs RefreshStore) *Service {
	return &Service{
		signingKey: signingKey,
		accessTTL:  accessTTL,
		refreshTTL: refreshTTL,
		rs:         rs,
		now:        time.Now,
	}
}

// IssueAccess mints an HS256 access token bound to account+family.
func (s *Service) IssueAccess(accountID, familyID string) (string, int64, error) {
	now := s.now()
	claims := AccessClaims{
		FamilyID: familyID,
		RegisteredClaims: jwt.RegisteredClaims{
			Subject:   accountID,
			Audience:  jwt.ClaimStrings{accessAudience},
			IssuedAt:  jwt.NewNumericDate(now),
			ExpiresAt: jwt.NewNumericDate(now.Add(s.accessTTL)),
		},
	}
	tok := jwt.NewWithClaims(jwt.SigningMethodHS256, claims)
	signed, err := tok.SignedString(s.signingKey)
	if err != nil {
		return "", 0, fmt.Errorf("token: sign access: %w", err)
	}
	return signed, int64(s.accessTTL.Seconds()), nil
}

// ParseAccess verifies an access token and returns its claims.
func (s *Service) ParseAccess(tokenStr string) (*AccessClaims, error) {
	claims := &AccessClaims{}
	_, err := jwt.ParseWithClaims(tokenStr, claims, func(t *jwt.Token) (any, error) {
		if _, ok := t.Method.(*jwt.SigningMethodHMAC); !ok {
			return nil, fmt.Errorf("token: unexpected signing method %v", t.Header["alg"])
		}
		return s.signingKey, nil
	}, jwt.WithAudience(accessAudience), jwt.WithValidMethods([]string{"HS256"}))
	if err != nil {
		return nil, ErrInvalidAccess
	}
	return claims, nil
}

// NewSession starts a fresh refresh-token family and returns the first pair.
func (s *Service) NewSession(ctx context.Context, accountID string) (*Pair, error) {
	familyID := store.NewUUID()
	return s.issuePair(ctx, accountID, familyID)
}

// Refresh rotates a presented refresh token. On reuse of an already-revoked
// token it revokes the whole family and returns ErrReuseDetected.
func (s *Service) Refresh(ctx context.Context, presented string) (*Pair, error) {
	hash := hashRefresh(presented)
	rt, err := s.rs.GetRefreshTokenByHash(ctx, hash)
	if errors.Is(err, store.ErrNotFound) {
		return nil, ErrInvalidRefresh
	}
	if err != nil {
		return nil, err
	}

	// Reuse detection: an already-revoked token being presented means the chain
	// was likely stolen — revoke the entire family (RFC 9700).
	if rt.RevokedAt != nil {
		if _, rerr := s.rs.RevokeFamily(ctx, rt.FamilyID); rerr != nil {
			return nil, rerr
		}
		return nil, ErrReuseDetected
	}
	if !rt.ExpiresAt.After(s.now()) {
		return nil, ErrInvalidRefresh
	}

	// Rotate: revoke old + insert successor atomically.
	plaintext, next, err := s.mintRefresh(rt.AccountID, rt.FamilyID)
	if err != nil {
		return nil, err
	}
	if err := s.rs.RotateRefreshToken(ctx, rt.TokenID, next); err != nil {
		if errors.Is(err, store.ErrNotFound) {
			// Lost the rotation race → concurrent reuse; revoke family.
			if _, rerr := s.rs.RevokeFamily(ctx, rt.FamilyID); rerr != nil {
				return nil, rerr
			}
			return nil, ErrReuseDetected
		}
		return nil, err
	}

	access, expiresIn, err := s.IssueAccess(rt.AccountID, rt.FamilyID)
	if err != nil {
		return nil, err
	}
	return &Pair{AccessToken: access, RefreshToken: plaintext, ExpiresIn: expiresIn, FamilyID: rt.FamilyID}, nil
}

// Logout revokes every live token in a family.
func (s *Service) Logout(ctx context.Context, familyID string) error {
	_, err := s.rs.RevokeFamily(ctx, familyID)
	return err
}

// issuePair mints an access token and a first refresh token for a family.
func (s *Service) issuePair(ctx context.Context, accountID, familyID string) (*Pair, error) {
	plaintext, rt, err := s.mintRefresh(accountID, familyID)
	if err != nil {
		return nil, err
	}
	if err := s.rs.InsertRefreshToken(ctx, rt); err != nil {
		return nil, err
	}
	access, expiresIn, err := s.IssueAccess(accountID, familyID)
	if err != nil {
		return nil, err
	}
	return &Pair{AccessToken: access, RefreshToken: plaintext, ExpiresIn: expiresIn, FamilyID: familyID}, nil
}

// mintRefresh generates a random opaque refresh token and its DB row (hashed).
func (s *Service) mintRefresh(accountID, familyID string) (string, store.RefreshToken, error) {
	buf := make([]byte, 32)
	if _, err := rand.Read(buf); err != nil {
		return "", store.RefreshToken{}, fmt.Errorf("token: rand refresh: %w", err)
	}
	plaintext := base64.RawURLEncoding.EncodeToString(buf)
	rt := store.RefreshToken{
		TokenID:   store.NewUUID(),
		AccountID: accountID,
		TokenHash: hashRefresh(plaintext),
		FamilyID:  familyID,
		ExpiresAt: s.now().Add(s.refreshTTL),
	}
	return plaintext, rt, nil
}

func hashRefresh(plaintext string) string {
	sum := sha256.Sum256([]byte(plaintext))
	return hex.EncodeToString(sum[:])
}

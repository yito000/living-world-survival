package token

import (
	"context"
	"testing"
	"time"

	"living-world-survival/services/auth/internal/store"
)

// fakeRefreshStore is an in-memory RefreshStore for unit-testing rotation and
// reuse detection without a database.
type fakeRefreshStore struct {
	byHash map[string]*store.RefreshToken
	byID   map[string]*store.RefreshToken
}

func newFakeStore() *fakeRefreshStore {
	return &fakeRefreshStore{
		byHash: map[string]*store.RefreshToken{},
		byID:   map[string]*store.RefreshToken{},
	}
}

func (f *fakeRefreshStore) InsertRefreshToken(_ context.Context, rt store.RefreshToken) error {
	cp := rt
	f.byHash[rt.TokenHash] = &cp
	f.byID[rt.TokenID] = &cp
	return nil
}

func (f *fakeRefreshStore) GetRefreshTokenByHash(_ context.Context, hash string) (*store.RefreshToken, error) {
	rt, ok := f.byHash[hash]
	if !ok {
		return nil, store.ErrNotFound
	}
	cp := *rt
	return &cp, nil
}

func (f *fakeRefreshStore) RotateRefreshToken(_ context.Context, oldTokenID string, next store.RefreshToken) error {
	old, ok := f.byID[oldTokenID]
	if !ok || old.RevokedAt != nil {
		return store.ErrNotFound
	}
	now := time.Now()
	old.RevokedAt = &now
	cp := next
	f.byHash[next.TokenHash] = &cp
	f.byID[next.TokenID] = &cp
	return nil
}

func (f *fakeRefreshStore) RevokeFamily(_ context.Context, familyID string) (int64, error) {
	var n int64
	now := time.Now()
	for _, rt := range f.byID {
		if rt.FamilyID == familyID && rt.RevokedAt == nil {
			rt.RevokedAt = &now
			n++
		}
	}
	return n, nil
}

func newSvc(fs RefreshStore) *Service {
	return NewService([]byte("test-signing-key"), 15*time.Minute, 720*time.Hour, fs)
}

func TestAccessTokenRoundTrip(t *testing.T) {
	s := newSvc(newFakeStore())
	tok, expiresIn, err := s.IssueAccess("acct-1", "fam-1")
	if err != nil {
		t.Fatalf("issue: %v", err)
	}
	if expiresIn != 900 {
		t.Fatalf("expiresIn: got %d want 900", expiresIn)
	}
	claims, err := s.ParseAccess(tok)
	if err != nil {
		t.Fatalf("parse: %v", err)
	}
	if claims.Subject != "acct-1" || claims.FamilyID != "fam-1" {
		t.Fatalf("claims mismatch: %+v", claims)
	}
}

func TestAccessTokenTamperRejected(t *testing.T) {
	s := newSvc(newFakeStore())
	tok, _, _ := s.IssueAccess("acct-1", "fam-1")
	tampered := tok[:len(tok)-2] + "xy"
	if _, err := s.ParseAccess(tampered); err != ErrInvalidAccess {
		t.Fatalf("tampered token: got %v want ErrInvalidAccess", err)
	}
}

func TestAccessTokenWrongKeyRejected(t *testing.T) {
	s := newSvc(newFakeStore())
	tok, _, _ := s.IssueAccess("acct-1", "fam-1")
	other := NewService([]byte("different-key"), time.Minute, time.Hour, newFakeStore())
	if _, err := other.ParseAccess(tok); err != ErrInvalidAccess {
		t.Fatalf("wrong key: got %v want ErrInvalidAccess", err)
	}
}

func TestRefreshRotation(t *testing.T) {
	ctx := context.Background()
	s := newSvc(newFakeStore())
	p1, err := s.NewSession(ctx, "acct-1")
	if err != nil {
		t.Fatalf("new session: %v", err)
	}
	p2, err := s.Refresh(ctx, p1.RefreshToken)
	if err != nil {
		t.Fatalf("rotate: %v", err)
	}
	if p2.RefreshToken == p1.RefreshToken {
		t.Fatal("rotation must yield a new refresh token")
	}
	if p2.FamilyID != p1.FamilyID {
		t.Fatal("rotation must stay in the same family")
	}
	// The new token continues to work.
	if _, err := s.Refresh(ctx, p2.RefreshToken); err != nil {
		t.Fatalf("rotate p2: %v", err)
	}
}

func TestRefreshReuseDetectionRevokesFamily(t *testing.T) {
	ctx := context.Background()
	fs := newFakeStore()
	s := newSvc(fs)
	p1, _ := s.NewSession(ctx, "acct-1")
	p2, _ := s.Refresh(ctx, p1.RefreshToken) // p1 now revoked

	// Presenting the old (revoked) token is reuse → family revoked.
	if _, err := s.Refresh(ctx, p1.RefreshToken); err != ErrReuseDetected {
		t.Fatalf("reuse: got %v want ErrReuseDetected", err)
	}
	// The whole family is dead now: even the latest token fails.
	if _, err := s.Refresh(ctx, p2.RefreshToken); err == nil {
		t.Fatal("expected latest token to be invalid after family revocation")
	}
}

func TestRefreshUnknownToken(t *testing.T) {
	s := newSvc(newFakeStore())
	if _, err := s.Refresh(context.Background(), "not-a-real-token"); err != ErrInvalidRefresh {
		t.Fatalf("unknown: got %v want ErrInvalidRefresh", err)
	}
}

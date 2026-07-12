package ticket

import (
	"crypto/ed25519"
	"crypto/rand"
	"testing"
	"time"

	survivalv1 "living-world-survival/services/gen/go/survival/v1"
)

func newTestSigner(t *testing.T, ttl time.Duration) (*Signer, ed25519.PublicKey) {
	t.Helper()
	pub, priv, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatalf("genkey: %v", err)
	}
	return NewSigner(priv, pub, ttl), pub
}

func TestIssueVerifyRoundTrip(t *testing.T) {
	s, _ := newTestSigner(t, 60*time.Second)
	claims, tok, err := s.Issue("acct-1", "11111111-1111-4111-8111-111111111111", "srv-1", "world-1", "build-42")
	if err != nil {
		t.Fatalf("issue: %v", err)
	}
	got, err := s.Verify(tok)
	if err != nil {
		t.Fatalf("verify: %v", err)
	}
	if got.GetTicketId() != claims.GetTicketId() ||
		got.GetAccountId() != "acct-1" ||
		got.GetCharacterId() != "11111111-1111-4111-8111-111111111111" ||
		got.GetServerId() != "srv-1" ||
		got.GetWorldId() != "world-1" ||
		got.GetBuildId() != "build-42" ||
		got.GetNonce() != claims.GetNonce() {
		t.Fatalf("claims mismatch: %+v vs %+v", got, claims)
	}
	if got.GetExpiresAtUnixMs() != claims.GetExpiresAtUnixMs() {
		t.Fatalf("exp mismatch: %d vs %d", got.GetExpiresAtUnixMs(), claims.GetExpiresAtUnixMs())
	}
}

func TestVerifyTamperedRejected(t *testing.T) {
	s, _ := newTestSigner(t, 60*time.Second)
	_, tok, _ := s.Issue("acct-1", "c", "srv-1", "world-1", "build-42")
	tampered := tok[:len(tok)-3] + "AAA"
	if _, err := s.Verify(tampered); err != ErrInvalid {
		t.Fatalf("tampered: got %v want ErrInvalid", err)
	}
}

func TestVerifyWrongKeyRejected(t *testing.T) {
	s, _ := newTestSigner(t, 60*time.Second)
	_, tok, _ := s.Issue("acct-1", "c", "srv-1", "world-1", "build-42")

	other, _ := newTestSigner(t, 60*time.Second) // different keypair
	if _, err := other.Verify(tok); err != ErrInvalid {
		t.Fatalf("wrong key: got %v want ErrInvalid", err)
	}
}

func TestVerifyExpiredRejected(t *testing.T) {
	s, _ := newTestSigner(t, 60*time.Second)
	// Sign claims that already expired.
	past := time.Now().Add(-2 * time.Minute)
	tok, err := s.Sign(&survivalv1.JoinTicketClaims{
		TicketId:        "t-1",
		AccountId:       "acct-1",
		ServerId:        "srv-1",
		IssuedAtUnixMs:  past.Add(-time.Minute).UnixMilli(),
		ExpiresAtUnixMs: past.UnixMilli(),
	})
	if err != nil {
		t.Fatalf("sign: %v", err)
	}
	if _, err := s.Verify(tok); err != ErrInvalid {
		t.Fatalf("expired: got %v want ErrInvalid", err)
	}
}

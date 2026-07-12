// Package ticket signs and verifies Join Tickets (MVP 11.3 / BSD第4.1). A
// ticket is the proto survival.v1.JoinTicketClaims encoded as an EdDSA (Ed25519)
// compact JWS. Auth holds the private key; the Dedicated Server pre-verifies
// with the public key. Single-use is enforced separately in the DB (3.4).
//
// Token format (shared with 04A): standard JWT compact serialization, header
// alg=EdDSA. Claims mapping — jti=ticket_id, sub=account_id, iat=issued_at,
// exp=expires_at, chr=character_id, srv=server_id, wld=world_id, bld=build_id,
// nonce=nonce.
package ticket

import (
	"crypto/ed25519"
	"crypto/rand"
	"encoding/base64"
	"errors"
	"fmt"
	"time"

	"github.com/golang-jwt/jwt/v5"

	survivalv1 "living-world-survival/services/gen/go/survival/v1"
)

// ErrInvalid is returned when a ticket fails signature or expiry verification.
var ErrInvalid = errors.New("ticket: invalid or expired join ticket")

type ticketClaims struct {
	CharacterID string `json:"chr"`
	ServerID    string `json:"srv"`
	WorldID     string `json:"wld"`
	BuildID     string `json:"bld"`
	Nonce       string `json:"nonce"`
	// Exact unix-millisecond timestamps from JoinTicketClaims. Standard JWT
	// iat/exp are second-granular, so these preserve the ms precision the proto
	// promises. The DS may read either (04A); expiry is enforced via std exp.
	IssuedAtMs  int64 `json:"iat_ms"`
	ExpiresAtMs int64 `json:"exp_ms"`
	jwt.RegisteredClaims
}

// Signer issues and verifies Join Tickets with an Ed25519 keypair.
type Signer struct {
	priv ed25519.PrivateKey
	pub  ed25519.PublicKey
	ttl  time.Duration
	now  func() time.Time
}

// NewSigner constructs a Signer. ttl is the Join Ticket lifetime (e.g. 60s).
func NewSigner(priv ed25519.PrivateKey, pub ed25519.PublicKey, ttl time.Duration) *Signer {
	return &Signer{priv: priv, pub: pub, ttl: ttl, now: time.Now}
}

// Issue builds fresh claims (ticket_id + nonce generated here), signs them, and
// returns both the proto claims (for DB persistence) and the token string.
func (s *Signer) Issue(accountID, characterID, serverID, worldID, buildID string) (*survivalv1.JoinTicketClaims, string, error) {
	now := s.now()
	nonce, err := randNonce()
	if err != nil {
		return nil, "", err
	}
	claims := &survivalv1.JoinTicketClaims{
		TicketId:        newUUID(),
		AccountId:       accountID,
		CharacterId:     characterID,
		ServerId:        serverID,
		WorldId:         worldID,
		BuildId:         buildID,
		IssuedAtUnixMs:  now.UnixMilli(),
		ExpiresAtUnixMs: now.Add(s.ttl).UnixMilli(),
		Nonce:           nonce,
	}
	tok, err := s.Sign(claims)
	if err != nil {
		return nil, "", err
	}
	return claims, tok, nil
}

// Sign encodes claims as an EdDSA JWS.
func (s *Signer) Sign(c *survivalv1.JoinTicketClaims) (string, error) {
	tc := ticketClaims{
		CharacterID: c.GetCharacterId(),
		ServerID:    c.GetServerId(),
		WorldID:     c.GetWorldId(),
		BuildID:     c.GetBuildId(),
		Nonce:       c.GetNonce(),
		IssuedAtMs:  c.GetIssuedAtUnixMs(),
		ExpiresAtMs: c.GetExpiresAtUnixMs(),
		RegisteredClaims: jwt.RegisteredClaims{
			ID:        c.GetTicketId(),
			Subject:   c.GetAccountId(),
			IssuedAt:  jwt.NewNumericDate(time.UnixMilli(c.GetIssuedAtUnixMs())),
			ExpiresAt: jwt.NewNumericDate(time.UnixMilli(c.GetExpiresAtUnixMs())),
		},
	}
	tok := jwt.NewWithClaims(jwt.SigningMethodEdDSA, tc)
	signed, err := tok.SignedString(s.priv)
	if err != nil {
		return "", fmt.Errorf("ticket: sign: %w", err)
	}
	return signed, nil
}

// Verify checks the signature and expiry and returns the decoded claims. It
// does NOT check single-use (that is the DB's job in RedeemJoinTicket).
func (s *Signer) Verify(token string) (*survivalv1.JoinTicketClaims, error) {
	tc := &ticketClaims{}
	_, err := jwt.ParseWithClaims(token, tc, func(t *jwt.Token) (any, error) {
		if _, ok := t.Method.(*jwt.SigningMethodEd25519); !ok {
			return nil, fmt.Errorf("ticket: unexpected signing method %v", t.Header["alg"])
		}
		return s.pub, nil
	}, jwt.WithValidMethods([]string{"EdDSA"}))
	if err != nil {
		return nil, ErrInvalid
	}
	return &survivalv1.JoinTicketClaims{
		TicketId:        tc.ID,
		AccountId:       tc.Subject,
		CharacterId:     tc.CharacterID,
		ServerId:        tc.ServerID,
		WorldId:         tc.WorldID,
		BuildId:         tc.BuildID,
		IssuedAtUnixMs:  tc.IssuedAtMs,
		ExpiresAtUnixMs: tc.ExpiresAtMs,
		Nonce:           tc.Nonce,
	}, nil
}

func randNonce() (string, error) {
	b := make([]byte, 16)
	if _, err := rand.Read(b); err != nil {
		return "", fmt.Errorf("ticket: rand nonce: %w", err)
	}
	return base64.RawURLEncoding.EncodeToString(b), nil
}

func newUUID() string {
	var b [16]byte
	if _, err := rand.Read(b[:]); err != nil {
		panic(fmt.Sprintf("ticket: uuid rand: %v", err))
	}
	b[6] = (b[6] & 0x0f) | 0x40
	b[8] = (b[8] & 0x3f) | 0x80
	return fmt.Sprintf("%x-%x-%x-%x-%x", b[0:4], b[4:6], b[6:8], b[8:10], b[10:16])
}

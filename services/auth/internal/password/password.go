// Package password hashes and verifies passwords with Argon2id (BSD第11章 /
// OWASP Password Storage). Hashes are stored in the standard PHC string
// format so parameters travel with the hash and can be tuned over time.
package password

import (
	"crypto/rand"
	"crypto/subtle"
	"encoding/base64"
	"errors"
	"fmt"
	"strings"

	"golang.org/x/crypto/argon2"
)

// Params for Argon2id. Defaults meet the OWASP minimum (>= 19 MiB, t>=2, p>=1).
type Params struct {
	Memory      uint32 // KiB
	Iterations  uint32
	Parallelism uint8
	SaltLen     uint32
	KeyLen      uint32
}

// DefaultParams is the OWASP-recommended baseline for Argon2id.
var DefaultParams = Params{
	Memory:      19 * 1024, // 19 MiB
	Iterations:  2,
	Parallelism: 1,
	SaltLen:     16,
	KeyLen:      32,
}

// ErrMismatch is returned by Verify when the password does not match the hash.
var ErrMismatch = errors.New("password: hash mismatch")

// Hash returns a PHC-formatted Argon2id hash of the password using DefaultParams.
func Hash(pw string) (string, error) {
	return HashWith(pw, DefaultParams)
}

// HashWith hashes with explicit params (used for faster tests).
func HashWith(pw string, p Params) (string, error) {
	salt := make([]byte, p.SaltLen)
	if _, err := rand.Read(salt); err != nil {
		return "", fmt.Errorf("password: read salt: %w", err)
	}
	key := argon2.IDKey([]byte(pw), salt, p.Iterations, p.Memory, p.Parallelism, p.KeyLen)
	return fmt.Sprintf("$argon2id$v=%d$m=%d,t=%d,p=%d$%s$%s",
		argon2.Version, p.Memory, p.Iterations, p.Parallelism,
		base64.RawStdEncoding.EncodeToString(salt),
		base64.RawStdEncoding.EncodeToString(key),
	), nil
}

// Verify compares a plaintext password against a stored PHC hash in constant
// time. It returns ErrMismatch on a valid-but-wrong password.
func Verify(pw, encoded string) error {
	p, salt, want, err := decode(encoded)
	if err != nil {
		return err
	}
	got := argon2.IDKey([]byte(pw), salt, p.Iterations, p.Memory, p.Parallelism, uint32(len(want)))
	if subtle.ConstantTimeCompare(got, want) != 1 {
		return ErrMismatch
	}
	return nil
}

func decode(encoded string) (Params, []byte, []byte, error) {
	var p Params
	parts := strings.Split(encoded, "$")
	// ["", "argon2id", "v=19", "m=...,t=...,p=...", salt, hash]
	if len(parts) != 6 || parts[1] != "argon2id" {
		return p, nil, nil, errors.New("password: invalid hash format")
	}
	var version int
	if _, err := fmt.Sscanf(parts[2], "v=%d", &version); err != nil {
		return p, nil, nil, fmt.Errorf("password: invalid version: %w", err)
	}
	if version != argon2.Version {
		return p, nil, nil, fmt.Errorf("password: unsupported argon2 version %d", version)
	}
	if _, err := fmt.Sscanf(parts[3], "m=%d,t=%d,p=%d", &p.Memory, &p.Iterations, &p.Parallelism); err != nil {
		return p, nil, nil, fmt.Errorf("password: invalid params: %w", err)
	}
	salt, err := base64.RawStdEncoding.DecodeString(parts[4])
	if err != nil {
		return p, nil, nil, fmt.Errorf("password: invalid salt: %w", err)
	}
	hash, err := base64.RawStdEncoding.DecodeString(parts[5])
	if err != nil {
		return p, nil, nil, fmt.Errorf("password: invalid hash: %w", err)
	}
	return p, salt, hash, nil
}

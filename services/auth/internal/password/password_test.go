package password

import (
	"strings"
	"testing"
)

// fastParams keep tests quick while staying Argon2id.
var fastParams = Params{Memory: 8 * 1024, Iterations: 1, Parallelism: 1, SaltLen: 16, KeyLen: 32}

func TestHashVerifyRoundTrip(t *testing.T) {
	h, err := HashWith("correct horse battery staple", fastParams)
	if err != nil {
		t.Fatalf("hash: %v", err)
	}
	if !strings.HasPrefix(h, "$argon2id$") {
		t.Fatalf("hash not PHC argon2id: %q", h)
	}
	if err := Verify("correct horse battery staple", h); err != nil {
		t.Fatalf("verify correct: %v", err)
	}
}

func TestVerifyWrongPassword(t *testing.T) {
	h, _ := HashWith("s3cret-password", fastParams)
	if err := Verify("wrong-password", h); err != ErrMismatch {
		t.Fatalf("verify wrong: got %v want ErrMismatch", err)
	}
}

func TestHashUniqueSalt(t *testing.T) {
	a, _ := HashWith("same", fastParams)
	b, _ := HashWith("same", fastParams)
	if a == b {
		t.Fatal("hashes of same password must differ (random salt)")
	}
}

func TestVerifyMalformed(t *testing.T) {
	if err := Verify("x", "not-a-phc-string"); err == nil {
		t.Fatal("expected error for malformed hash")
	}
}

func TestDefaultParamsMeetOWASP(t *testing.T) {
	// OWASP Argon2id minimum: memory >= 19 MiB, iterations >= 2, parallelism >= 1.
	if DefaultParams.Memory < 19*1024 {
		t.Fatalf("memory %d KiB below OWASP minimum", DefaultParams.Memory)
	}
	if DefaultParams.Iterations < 2 {
		t.Fatalf("iterations %d below OWASP minimum", DefaultParams.Iterations)
	}
	if DefaultParams.Parallelism < 1 {
		t.Fatalf("parallelism %d below minimum", DefaultParams.Parallelism)
	}
}

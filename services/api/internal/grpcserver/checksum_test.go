package grpcserver

import (
	"crypto/sha256"
	"encoding/hex"
	"strings"
	"testing"
)

func TestChecksumMatches(t *testing.T) {
	payload := []byte(`{"world":"demo","tick":42}`)
	sum := sha256.Sum256(payload)
	good := hex.EncodeToString(sum[:])

	if !checksumMatches(payload, good) {
		t.Error("correct lowercase checksum should match")
	}
	// A/B interop tolerance: uppercase hex from the DS still matches.
	if !checksumMatches(payload, strings.ToUpper(good)) {
		t.Error("uppercase checksum should match (case-insensitive)")
	}
	if checksumMatches(payload, "") {
		t.Error("empty checksum must be rejected")
	}
	if checksumMatches(payload, "deadbeef") {
		t.Error("wrong checksum must be rejected")
	}
	// Tampered payload no longer matches the original digest.
	if checksumMatches([]byte(`{"world":"demo","tick":43}`), good) {
		t.Error("tampered payload must not match")
	}
}

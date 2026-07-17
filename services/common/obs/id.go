package obs

import (
	"crypto/rand"
	"encoding/hex"
	"log/slog"
)

// NewCorrelationID は 1 リクエストを貫く相関 ID を採番する（第13章）。
// domain_events の event_id（ULID / DS 採番）とは別物で、こちらはログ相関専用。
func NewCorrelationID() string {
	var b [8]byte
	if _, err := rand.Read(b[:]); err != nil {
		// crypto/rand が読めないのは異常事態。ID が無いよりは固定値の方が
		// 「相関が取れていない」と気付けるので、握って続行する。
		return "cid-unavailable"
	}
	return hex.EncodeToString(b[:])
}

func slogWriteErr(err error) {
	slog.Default().Warn("write response failed", "error", err.Error())
}

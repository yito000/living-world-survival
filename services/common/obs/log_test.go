package obs

import (
	"bytes"
	"context"
	"encoding/json"
	"log/slog"
	"strings"
	"testing"
)

// newCapture は redactAttr を効かせたまま出力を捕まえる logger を返す。
func newCapture(t *testing.T) (*slog.Logger, *bytes.Buffer) {
	t.Helper()
	var buf bytes.Buffer
	h := slog.NewJSONHandler(&buf, &slog.HandlerOptions{ReplaceAttr: redactAttr})
	return slog.New(h), &buf
}

// MVP-SEC-002: password/token の類は「書いても出ない」ことを保証する。
func TestRedactsSecretAttrs(t *testing.T) {
	l, buf := newCapture(t)
	l.Info("login",
		"password", "hunter2",
		"refresh_token", "rt-abc",
		"Authorization", "Bearer xyz",
		"API_KEY", "sk-live-123",
		"account_id", "acc-1",
	)
	out := buf.String()
	for _, leaked := range []string{"hunter2", "rt-abc", "Bearer xyz", "sk-live-123"} {
		if strings.Contains(out, leaked) {
			t.Fatalf("secret leaked into log output: %q in %s", leaked, out)
		}
	}
	if !strings.Contains(out, "acc-1") {
		t.Fatalf("non-secret field should survive redaction: %s", out)
	}
	if strings.Count(out, Redacted) != 4 {
		t.Fatalf("expected 4 redacted values, got %s", out)
	}
}

func TestIsRedactedKeyNaming(t *testing.T) {
	// キー命名の揺れ（大小文字/区切り）を跨いで判定できること。
	for _, k := range []string{
		"password", "Password", "PASSWD", "refresh_token", "refreshToken",
		"refresh-token", "access_token", "api_key", "apiKey", "signing_key",
		"private_key", "secret", "Authorization",
	} {
		if !isRedactedKey(k) {
			t.Errorf("expected %q to be redacted", k)
		}
	}
	// 正当なフィールドまで巻き込んで伏せてはならない。
	for _, k := range []string{
		"account_id", "actor_id", "world_id", "server_id", "correlation_id",
		"status", "method", "duration_ms", "route",
	} {
		if isRedactedKey(k) {
			t.Errorf("expected %q NOT to be redacted", k)
		}
	}
}

func TestFieldsRoundTripThroughContext(t *testing.T) {
	ctx := WithFields(context.Background(), Fields{WorldID: "w1", CorrelationID: "c1"})
	// 後から重ねたフィールドは、既存の非空フィールドを消さずに追加される。
	ctx = WithFields(ctx, Fields{ActorID: "a1"})

	got := FieldsOf(ctx)
	if got.WorldID != "w1" || got.CorrelationID != "c1" || got.ActorID != "a1" {
		t.Fatalf("unexpected fields: %+v", got)
	}
}

func TestMergeOnlyOverwritesNonEmpty(t *testing.T) {
	base := Fields{WorldID: "w1", AccountID: "acc-1"}
	got := base.Merge(Fields{AccountID: "acc-2", ActorID: "a1"})

	if got.WorldID != "w1" {
		t.Errorf("empty field must not clear existing value, got %q", got.WorldID)
	}
	if got.AccountID != "acc-2" {
		t.Errorf("non-empty field must overwrite, got %q", got.AccountID)
	}
	if got.ActorID != "a1" {
		t.Errorf("new field should be set, got %q", got.ActorID)
	}
}

func TestAttrsSkipsEmptyFields(t *testing.T) {
	attrs := Fields{WorldID: "w1"}.Attrs()
	if len(attrs) != 1 {
		t.Fatalf("expected only the non-empty field, got %v", attrs)
	}
}

// L(ctx) が相関フィールドを実際に JSON へ乗せることを確認する（第13章）。
func TestLoggerEmitsCorrelationFields(t *testing.T) {
	l, buf := newCapture(t)
	old := slog.Default()
	slog.SetDefault(l)
	t.Cleanup(func() { slog.SetDefault(old) })

	ctx := WithFields(context.Background(), Fields{
		WorldID: "w1", ServerID: "s1", AccountID: "acc-1",
		ActorID: "a1", CorrelationID: "c1",
	})
	L(ctx).Info("purchase committed")

	var got map[string]any
	if err := json.Unmarshal(buf.Bytes(), &got); err != nil {
		t.Fatalf("log line is not valid JSON: %v (%s)", err, buf.String())
	}
	for k, want := range map[string]string{
		FieldWorldID: "w1", FieldServerID: "s1", FieldAccountID: "acc-1",
		FieldActorID: "a1", FieldCorrelationID: "c1",
	} {
		if got[k] != want {
			t.Errorf("field %s = %v, want %s", k, got[k], want)
		}
	}
}

func TestParseLevel(t *testing.T) {
	for in, want := range map[string]slog.Level{
		"debug": slog.LevelDebug, "DEBUG": slog.LevelDebug,
		"warn": slog.LevelWarn, "warning": slog.LevelWarn,
		"error": slog.LevelError, "info": slog.LevelInfo,
		"": slog.LevelInfo, "nonsense": slog.LevelInfo, " Debug ": slog.LevelDebug,
	} {
		if got := parseLevel(in); got != want {
			t.Errorf("parseLevel(%q) = %v, want %v", in, got, want)
		}
	}
}

func TestNewCorrelationIDIsUnique(t *testing.T) {
	seen := map[string]bool{}
	for i := 0; i < 100; i++ {
		id := NewCorrelationID()
		if id == "" || id == "cid-unavailable" {
			t.Fatalf("bad correlation id: %q", id)
		}
		if seen[id] {
			t.Fatalf("duplicate correlation id: %s", id)
		}
		seen[id] = true
	}
}

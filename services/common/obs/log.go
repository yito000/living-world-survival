// Package obs は全 Go サービス共通の可観測性（JSON 構造化ログ / Prometheus metrics /
// health）を提供する（10B 3.5・基本設計第13章）。api と auth で同じ実装を二重に持たない
// ための共有モジュール。
package obs

import (
	"context"
	"log/slog"
	"os"
	"strings"
)

// 第13章が要求する構造化ログの相関フィールド。ログ側もハーネス側もこの名前を正とする。
const (
	FieldWorldID       = "world_id"
	FieldServerID      = "server_id"
	FieldAccountID     = "account_id"
	FieldActorID       = "actor_id"
	FieldCorrelationID = "correlation_id"
)

// ctxKey は context に載せる相関フィールドのキー。
type ctxKey struct{}

// redactedKeys は値を絶対にログへ出してはならない属性キー（MVP-SEC-002）。
// scripts/log_secret_scan.sh はログ本文を走査するが、そもそも出さないのが一次防御。
var redactedKeys = []string{
	"password", "passwd", "secret", "token", "refresh", "authorization",
	"access_token", "refresh_token", "api_key", "signing_key", "private_key",
}

// Redacted は redact 後に出力される固定文字列。log_secret_scan.sh はこの文字列を
// 「正しく伏せられた」印として許容する。
const Redacted = "[REDACTED]"

// Init は service 名付きの JSON 構造化ログを default logger に設定して返す
// （基本設計第13章 Logging）。LOG_LEVEL で閾値を変えられる（既定 info）。
//
// 秘匿値は ReplaceAttr で機械的に伏せる。呼び出し側が誤って
// slog.String("password", pw) と書いても値は出ない（MVP-SEC-002）。
func Init(service string) *slog.Logger {
	h := slog.NewJSONHandler(os.Stdout, &slog.HandlerOptions{
		Level:       parseLevel(os.Getenv("LOG_LEVEL")),
		ReplaceAttr: redactAttr,
	})
	l := slog.New(h).With(slog.String("service", service))
	slog.SetDefault(l)
	return l
}

func parseLevel(s string) slog.Level {
	switch strings.ToLower(strings.TrimSpace(s)) {
	case "debug":
		return slog.LevelDebug
	case "warn", "warning":
		return slog.LevelWarn
	case "error":
		return slog.LevelError
	default:
		return slog.LevelInfo
	}
}

// redactAttr は秘匿キーの値を Redacted へ差し替える。キー名は大小文字と
// 区切り文字を無視して部分一致で判定する（"RefreshToken" / "refresh_token" 双方）。
func redactAttr(_ []string, a slog.Attr) slog.Attr {
	if isRedactedKey(a.Key) {
		return slog.String(a.Key, Redacted)
	}
	return a
}

func isRedactedKey(key string) bool {
	norm := strings.ToLower(key)
	norm = strings.NewReplacer("-", "", "_", "", ".", "").Replace(norm)
	for _, k := range redactedKeys {
		if strings.Contains(norm, strings.ReplaceAll(k, "_", "")) {
			return true
		}
	}
	return false
}

// Fields は 1 リクエスト/1 処理に紐づく相関フィールド。空文字のフィールドは出力しない。
type Fields struct {
	WorldID       string
	ServerID      string
	AccountID     string
	ActorID       string
	CorrelationID string
}

// Attrs は非空フィールドだけを slog 属性へ変換する。
func (f Fields) Attrs() []any {
	var out []any
	add := func(k, v string) {
		if v != "" {
			out = append(out, slog.String(k, v))
		}
	}
	add(FieldWorldID, f.WorldID)
	add(FieldServerID, f.ServerID)
	add(FieldAccountID, f.AccountID)
	add(FieldActorID, f.ActorID)
	add(FieldCorrelationID, f.CorrelationID)
	return out
}

// Merge は非空のフィールドだけを other で上書きした新しい Fields を返す。
func (f Fields) Merge(other Fields) Fields {
	if other.WorldID != "" {
		f.WorldID = other.WorldID
	}
	if other.ServerID != "" {
		f.ServerID = other.ServerID
	}
	if other.AccountID != "" {
		f.AccountID = other.AccountID
	}
	if other.ActorID != "" {
		f.ActorID = other.ActorID
	}
	if other.CorrelationID != "" {
		f.CorrelationID = other.CorrelationID
	}
	return f
}

// WithFields は ctx の既存フィールドへ f を重ねた context を返す。
func WithFields(ctx context.Context, f Fields) context.Context {
	return context.WithValue(ctx, ctxKey{}, FieldsOf(ctx).Merge(f))
}

// FieldsOf は ctx に載っている相関フィールドを返す（無ければゼロ値）。
func FieldsOf(ctx context.Context) Fields {
	if f, ok := ctx.Value(ctxKey{}).(Fields); ok {
		return f
	}
	return Fields{}
}

// L は ctx の相関フィールドを付けた logger を返す。ハンドラ内は常にこれを使う。
func L(ctx context.Context) *slog.Logger {
	return slog.Default().With(FieldsOf(ctx).Attrs()...)
}

package obs

import (
	"context"
	"strings"
	"time"

	"github.com/jackc/pgx/v5"
)

// tracerKey は TraceQueryStart→TraceQueryEnd 間で開始時刻を運ぶ context キー。
type tracerKey struct{}

// QueryTracer は全ての SQL 往復を db_query_duration_seconds へ記録する
// pgx.QueryTracer（基本設計第13章 DB latency / 10B 3.5）。
//
// 各 store メソッドに計測を撒くのではなく pool へ 1 箇所で挿すのは、
// 撒き忘れた経路が黙って計測から漏れるのを防ぐため。
type QueryTracer struct{}

// TraceQueryStart は開始時刻を context に載せる。
func (QueryTracer) TraceQueryStart(
	ctx context.Context, _ *pgx.Conn, _ pgx.TraceQueryStartData,
) context.Context {
	return context.WithValue(ctx, tracerKey{}, time.Now())
}

// TraceQueryEnd は所要時間を記録する。
func (QueryTracer) TraceQueryEnd(ctx context.Context, _ *pgx.Conn, data pgx.TraceQueryEndData) {
	start, ok := ctx.Value(tracerKey{}).(time.Time)
	if !ok {
		return
	}
	DBDuration.WithLabelValues(sqlOp(data.CommandTag.String())).
		Observe(time.Since(start).Seconds())
}

// sqlOp は SQL の種別（select/insert/...）をラベルにする。
//
// SQL 文そのものをラベルにすると、クエリの種類だけ時系列が増える上に、
// リテラルを含む文なら無限に増える。種別なら有界で、第13章の
// 「DB latency」を見る用途には十分。
func sqlOp(commandTag string) string {
	// CommandTag は "SELECT 1" / "INSERT 0 1" / "UPDATE 3" のような文字列。
	// 失敗したクエリでは空になり得る。
	field := strings.ToLower(strings.TrimSpace(commandTag))
	if field == "" {
		return "other"
	}
	if i := strings.IndexByte(field, ' '); i > 0 {
		field = field[:i]
	}
	switch field {
	case "select", "insert", "update", "delete", "begin", "commit", "rollback":
		return field
	default:
		return "other"
	}
}

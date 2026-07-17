package obs

import (
	"context"
	"testing"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"
	"github.com/prometheus/client_golang/prometheus/testutil"
)

// ラベルは有界でなければならない。SQL 文をそのまま載せると時系列が際限なく増える。
func TestSQLOpIsBounded(t *testing.T) {
	for in, want := range map[string]string{
		"SELECT 1":                    "select",
		"INSERT 0 1":                  "insert",
		"UPDATE 3":                    "update",
		"DELETE 2":                    "delete",
		"BEGIN":                       "begin",
		"COMMIT":                      "commit",
		"ROLLBACK":                    "rollback",
		"":                            "other",
		"   ":                         "other",
		"CREATE TABLE":                "other",
		"SELECT 1; DROP TABLE worlds": "select",
	} {
		if got := sqlOp(in); got != want {
			t.Errorf("sqlOp(%q) = %q, want %q", in, got, want)
		}
	}
}

// Start→End で所要時間が db_query_duration_seconds に載ること。
func TestQueryTracerObservesDuration(t *testing.T) {
	before := testutil.CollectAndCount(DBDuration)

	var tr QueryTracer
	ctx := tr.TraceQueryStart(context.Background(), nil, pgx.TraceQueryStartData{
		SQL: "SELECT 1",
	})
	time.Sleep(2 * time.Millisecond)
	tr.TraceQueryEnd(ctx, nil, pgx.TraceQueryEndData{
		CommandTag: pgconn.NewCommandTag("SELECT 1"),
	})

	if got := testutil.CollectAndCount(DBDuration); got <= before {
		t.Fatalf("expected a new db_query_duration_seconds series, got %d (was %d)", got, before)
	}
}

// Start を経ずに End が来ても panic せず、偽の観測も足さない。
func TestQueryTracerIgnoresEndWithoutStart(t *testing.T) {
	var tr QueryTracer
	before := testutil.CollectAndCount(DBDuration)

	tr.TraceQueryEnd(context.Background(), nil, pgx.TraceQueryEndData{
		CommandTag: pgconn.NewCommandTag("UPDATE 1"),
	})

	if got := testutil.CollectAndCount(DBDuration); got != before {
		t.Fatalf("End without Start must not record anything: %d -> %d", before, got)
	}
}

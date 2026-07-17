package obs

import (
	"net/http"
	"time"

	"github.com/prometheus/client_golang/prometheus"
	"github.com/prometheus/client_golang/prometheus/collectors"
	"github.com/prometheus/client_golang/prometheus/promhttp"
)

// registry は各サービス専用のレジストリ。default registry を使わないのは、
// 取り込んだライブラリが勝手に登録した系列で /metrics が汚れるのを避けるため。
var registry = prometheus.NewRegistry()

// Registry は本サービスの Prometheus レジストリを返す。サービス固有の
// Collector はここへ登録する（例: api の outbox depth）。
func Registry() *prometheus.Registry { return registry }

// レイテンシ Gate（10B 3.1）は P95/P99 を見る。ヒストグラムの bucket は
// 「操作 P95≤200ms / 購入 P95≤500ms / Tick P95≤40ms」の判定境界を跨ぐよう、
// 5ms〜10s を対数的に刻む。境界そのもの（0.04/0.2/0.5）を bucket 境界に含めて
// おくと、補間ではなく実測で Gate 判定できる。
// float64 は Prometheus のヒストグラム境界の型で、通貨・数量ではない（MVP 13.1 の
// 禁止対象外）。
//
//nolint:forbidigo // Prometheus bucket boundaries are seconds, not money.
var latencyBuckets = []float64{
	0.005, 0.01, 0.02, 0.04, 0.05, 0.075, 0.1, 0.15, 0.2, 0.3, 0.5, 0.75, 1, 2.5, 5, 10,
}

var (
	// HTTPDuration は REST ハンドラの処理時間（auth の /v1/... など）。
	HTTPDuration = prometheus.NewHistogramVec(prometheus.HistogramOpts{
		Name:    "http_request_duration_seconds",
		Help:    "HTTP handler duration in seconds.",
		Buckets: latencyBuckets,
	}, []string{"method", "route", "status"})

	// GRPCDuration は内部 gRPC ハンドラの処理時間。10B 3.1 の
	// 「通常操作 P95≤200ms」「購入 P95≤500ms（DB commit 含む）」はこの系列で判定する。
	GRPCDuration = prometheus.NewHistogramVec(prometheus.HistogramOpts{
		Name:    "grpc_server_handling_seconds",
		Help:    "gRPC unary handler duration in seconds.",
		Buckets: latencyBuckets,
	}, []string{"method", "code"})

	// DBDuration は DB 往復の所要時間（第13章 DB latency）。
	DBDuration = prometheus.NewHistogramVec(prometheus.HistogramOpts{
		Name:    "db_query_duration_seconds",
		Help:    "Database query duration in seconds.",
		Buckets: latencyBuckets,
	}, []string{"op"})
)

func init() {
	registry.MustRegister(
		collectors.NewGoCollector(),
		collectors.NewProcessCollector(collectors.ProcessCollectorOpts{}),
		HTTPDuration,
		GRPCDuration,
		DBDuration,
	)
}

// MetricsHandler は Prometheus exposition 形式の /metrics ハンドラを返す（R-PROM）。
func MetricsHandler() http.Handler {
	return promhttp.HandlerFor(registry, promhttp.HandlerOpts{Registry: registry})
}

// ObserveDB は DB 操作 1 回の所要時間を記録する。
//
//	defer obs.ObserveDB("purchase_commit")()
func ObserveDB(op string) func() {
	start := time.Now()
	return func() {
		DBDuration.WithLabelValues(op).Observe(time.Since(start).Seconds())
	}
}

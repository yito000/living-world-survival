package obs

import (
	"context"
	"encoding/json"
	"net/http"
	"strconv"
	"strings"
	"time"
)

// CorrelationHeader はサービス境界を越えて相関 ID を運ぶヘッダ。
// DS→auth→api の経路で同じ ID を引き継ぐと、第13章の相関ログが成立する。
const CorrelationHeader = "X-Correlation-Id"

// Check は readiness の依存チェック 1 件（DB / NATS など）。
// 基本設計第13章の「dependency health」を liveness と分離するための単位。
type Check struct {
	Name  string
	Probe func(context.Context) error
}

// LivenessHandler は /healthz（プロセスが生きているか）を返す。
// 依存先は見ない: DB が落ちた程度で再起動されてはならない。
func LivenessHandler(service string) http.HandlerFunc {
	return func(w http.ResponseWriter, _ *http.Request) {
		WriteJSON(w, http.StatusOK, map[string]string{"status": "ok", "service": service})
	}
}

// ReadinessHandler は /readyz（依存先を含めてトラフィックを受けられるか）を返す。
// 1 件でも失敗したら 503 + 落ちている dependency 名を返す（第13章 / 10B 3.5）。
func ReadinessHandler(checks ...Check) http.HandlerFunc {
	names := make([]string, 0, len(checks))
	for _, c := range checks {
		names = append(names, c.Name)
	}
	all := strings.Join(names, ",")

	return func(w http.ResponseWriter, r *http.Request) {
		ctx, cancel := context.WithTimeout(r.Context(), 2*time.Second)
		defer cancel()

		for _, c := range checks {
			if err := c.Probe(ctx); err != nil {
				// 依存先の失敗は運用上の常態でもあるので warn 止まり。
				// err はそのまま出す（依存先の接続エラーに秘匿値は載らない）。
				L(ctx).Warn("readiness check failed",
					"dependency", c.Name, "error", err.Error())
				WriteJSON(w, http.StatusServiceUnavailable, map[string]string{
					"status": "unavailable", "dependency": c.Name,
				})
				return
			}
		}
		WriteJSON(w, http.StatusOK, map[string]string{"status": "ready", "dependency": all})
	}
}

// WriteJSON は任意の値を JSON で返す。
func WriteJSON(w http.ResponseWriter, status int, body any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	if err := json.NewEncoder(w).Encode(body); err != nil {
		slogWriteErr(err)
	}
}

// statusRecorder は WriteHeader を覗いてステータスコードを記録する。
type statusRecorder struct {
	http.ResponseWriter
	status int
}

func (r *statusRecorder) WriteHeader(code int) {
	r.status = code
	r.ResponseWriter.WriteHeader(code)
}

func (r *statusRecorder) Write(b []byte) (int, error) {
	if r.status == 0 {
		r.status = http.StatusOK
	}
	return r.ResponseWriter.Write(b)
}

// Middleware は相関 ID の採番/引き継ぎと、レイテンシ計測・アクセスログを付ける。
// route はカーディナリティ爆発を避けるためテンプレート（実パスではない）を渡す。
func Middleware(route func(*http.Request) string) func(http.Handler) http.Handler {
	return func(next http.Handler) http.Handler {
		return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			cid := r.Header.Get(CorrelationHeader)
			if cid == "" {
				cid = NewCorrelationID()
			}
			ctx := WithFields(r.Context(), Fields{CorrelationID: cid})
			w.Header().Set(CorrelationHeader, cid)

			rec := &statusRecorder{ResponseWriter: w}
			start := time.Now()
			next.ServeHTTP(rec, r.WithContext(ctx))
			elapsed := time.Since(start)

			if rec.status == 0 {
				rec.status = http.StatusOK
			}
			rt := route(r)
			HTTPDuration.WithLabelValues(r.Method, rt, strconv.Itoa(rec.status)).
				Observe(elapsed.Seconds())

			// health/metrics のポーリングでログを埋めない。
			if rt == "/healthz" || rt == "/readyz" || rt == "/metrics" {
				return
			}
			L(ctx).Info("http request",
				"method", r.Method, "route", rt, "status", rec.status,
				"duration_ms", elapsed.Milliseconds())
		})
	}
}

package obs

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func decode(t *testing.T, body []byte) map[string]string {
	t.Helper()
	var m map[string]string
	if err := json.Unmarshal(body, &m); err != nil {
		t.Fatalf("body is not JSON: %v (%s)", err, body)
	}
	return m
}

func TestLivenessIgnoresDependencies(t *testing.T) {
	rec := httptest.NewRecorder()
	LivenessHandler("api")(rec, httptest.NewRequest(http.MethodGet, "/healthz", nil))

	if rec.Code != http.StatusOK {
		t.Fatalf("liveness must stay 200 regardless of deps, got %d", rec.Code)
	}
	if got := decode(t, rec.Body.Bytes()); got["service"] != "api" || got["status"] != "ok" {
		t.Fatalf("unexpected body: %v", got)
	}
}

func okCheck(name string) Check {
	return Check{Name: name, Probe: func(context.Context) error { return nil }}
}

func failCheck(name string) Check {
	return Check{Name: name, Probe: func(context.Context) error { return errors.New("down") }}
}

func TestReadinessAllChecksPass(t *testing.T) {
	rec := httptest.NewRecorder()
	ReadinessHandler(okCheck("postgres"), okCheck("nats"))(
		rec, httptest.NewRequest(http.MethodGet, "/readyz", nil))

	if rec.Code != http.StatusOK {
		t.Fatalf("expected 200, got %d", rec.Code)
	}
	if got := decode(t, rec.Body.Bytes()); got["dependency"] != "postgres,nats" {
		t.Fatalf("readyz should list every dependency it checked, got %v", got)
	}
}

// 503 は「どの依存が落ちたか」を名指しできないと運用で使えない（第13章）。
func TestReadinessReportsFailingDependency(t *testing.T) {
	rec := httptest.NewRecorder()
	ReadinessHandler(okCheck("postgres"), failCheck("nats"))(
		rec, httptest.NewRequest(http.MethodGet, "/readyz", nil))

	if rec.Code != http.StatusServiceUnavailable {
		t.Fatalf("expected 503, got %d", rec.Code)
	}
	got := decode(t, rec.Body.Bytes())
	if got["status"] != "unavailable" || got["dependency"] != "nats" {
		t.Fatalf("expected the failing dependency to be named, got %v", got)
	}
}

func TestMiddlewareGeneratesCorrelationID(t *testing.T) {
	var seen string
	h := Middleware(func(*http.Request) string { return "/v1/sessions" })(
		http.HandlerFunc(func(_ http.ResponseWriter, r *http.Request) {
			seen = FieldsOf(r.Context()).CorrelationID
		}))

	rec := httptest.NewRecorder()
	h.ServeHTTP(rec, httptest.NewRequest(http.MethodPost, "/v1/sessions", nil))

	if seen == "" {
		t.Fatal("handler context should carry a generated correlation id")
	}
	if got := rec.Header().Get(CorrelationHeader); got != seen {
		t.Fatalf("response header %q should echo the context id %q", got, seen)
	}
}

// 相関 ID は境界を越えて引き継ぐ（DS→auth→api で同じ ID）。
func TestMiddlewarePropagatesInboundCorrelationID(t *testing.T) {
	var seen string
	h := Middleware(func(*http.Request) string { return "/v1/sessions" })(
		http.HandlerFunc(func(_ http.ResponseWriter, r *http.Request) {
			seen = FieldsOf(r.Context()).CorrelationID
		}))

	req := httptest.NewRequest(http.MethodPost, "/v1/sessions", nil)
	req.Header.Set(CorrelationHeader, "upstream-cid")
	h.ServeHTTP(httptest.NewRecorder(), req)

	if seen != "upstream-cid" {
		t.Fatalf("inbound correlation id should be reused, got %q", seen)
	}
}

func TestMetricsHandlerExposesPrometheusFormat(t *testing.T) {
	// 系列が 1 つも無いと /metrics が空で通ってしまうので、先に 1 件記録する。
	HTTPDuration.WithLabelValues("GET", "/probe", "200").Observe(0.01)

	rec := httptest.NewRecorder()
	MetricsHandler().ServeHTTP(rec, httptest.NewRequest(http.MethodGet, "/metrics", nil))

	if rec.Code != http.StatusOK {
		t.Fatalf("expected 200, got %d", rec.Code)
	}
	body := rec.Body.String()
	// R-PROM: exposition 形式の HELP/TYPE と、負荷ハーネスが読む系列名。
	for _, want := range []string{
		"# TYPE http_request_duration_seconds histogram",
		`http_request_duration_seconds_bucket{method="GET",route="/probe",status="200"`,
		"go_goroutines",
	} {
		if !strings.Contains(body, want) {
			t.Errorf("missing %q in /metrics output", want)
		}
	}
}

// Gate 判定（P95≤40ms/200ms/500ms）を補間ではなく実測で行うため、
// 境界そのものが bucket 境界に含まれている必要がある（10B 3.1）。
//
//nolint:forbidigo // Gate boundaries are seconds, not money.
func TestLatencyBucketsCoverGateBoundaries(t *testing.T) {
	for _, gate := range []float64{0.04, 0.05, 0.2, 0.5} {
		found := false
		for _, b := range latencyBuckets {
			if b == gate {
				found = true
				break
			}
		}
		if !found {
			t.Errorf("gate boundary %vs is not a histogram bucket boundary", gate)
		}
	}
}

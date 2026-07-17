package main

import (
	"net/http"
	"net/http/httptest"
	"testing"

	"living-world-survival/services/common/obs"
)

func TestHealthz(t *testing.T) {
	mux := http.NewServeMux()
	mux.HandleFunc("/healthz", obs.LivenessHandler("auth"))

	req := httptest.NewRequest(http.MethodGet, "/healthz", nil)
	rec := httptest.NewRecorder()
	mux.ServeHTTP(rec, req)

	if rec.Code != http.StatusOK {
		t.Fatalf("healthz status: got %d want %d", rec.Code, http.StatusOK)
	}
	if ct := rec.Header().Get("Content-Type"); ct != "application/json" {
		t.Fatalf("healthz content-type: got %q", ct)
	}
}

// route ラベルは既知パスだけを通す。未知パスをそのまま label にすると、
// 存在しない URL を叩かれるだけで時系列が増え続ける。
func TestRouteLabelBoundsCardinality(t *testing.T) {
	for _, path := range []string{
		"/healthz", "/readyz", "/metrics",
		"/v1/accounts", "/v1/sessions", "/v1/sessions/refresh",
		"/v1/sessions/current", "/v1/matchmaking/join",
	} {
		req := httptest.NewRequest(http.MethodGet, path, nil)
		if got := routeLabel(req); got != path {
			t.Errorf("routeLabel(%q) = %q, want the path itself", path, got)
		}
	}

	for _, path := range []string{
		"/v1/sessions/../../etc/passwd", "/wp-admin.php", "/v1/unknown", "/",
	} {
		req := httptest.NewRequest(http.MethodGet, path, nil)
		if got := routeLabel(req); got != "other" {
			t.Errorf("routeLabel(%q) = %q, want \"other\"", path, got)
		}
	}
}

// knownRoutes と実際に mux へ登録したパスがずれると、正規の
// エンドポイントが "other" に落ちて metrics が使い物にならなくなる。
func TestKnownRoutesCoverServedPaths(t *testing.T) {
	// rest.Server が公開する /v1/... （services/auth/internal/rest/rest.go）。
	for _, path := range []string{
		"/v1/accounts", "/v1/sessions", "/v1/sessions/refresh",
		"/v1/sessions/current", "/v1/matchmaking/join",
	} {
		if !knownRoutes[path] {
			t.Errorf("REST route %q is served but missing from knownRoutes", path)
		}
	}
}

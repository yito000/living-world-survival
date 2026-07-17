package main

import (
	"net/http"
	"net/http/httptest"
	"testing"

	"living-world-survival/services/common/obs"
)

func TestHealthz(t *testing.T) {
	mux := http.NewServeMux()
	mux.HandleFunc("/healthz", obs.LivenessHandler("api"))

	req := httptest.NewRequest(http.MethodGet, "/healthz", nil)
	rec := httptest.NewRecorder()
	mux.ServeHTTP(rec, req)

	if rec.Code != http.StatusOK {
		t.Fatalf("healthz status: got %d want %d", rec.Code, http.StatusOK)
	}
}

// route ラベルは既知パスだけを通す。未知パスをそのまま label にすると、
// 存在しない URL を叩かれるだけで時系列が増え続ける。
func TestRouteLabelBoundsCardinality(t *testing.T) {
	for _, path := range []string{"/healthz", "/readyz", "/metrics", "/admin/ranking/run"} {
		req := httptest.NewRequest(http.MethodGet, path, nil)
		if got := routeLabel(req); got != path {
			t.Errorf("routeLabel(%q) = %q, want the path itself", path, got)
		}
	}
	for _, path := range []string{"/admin/../etc", "/unknown", "/"} {
		req := httptest.NewRequest(http.MethodGet, path, nil)
		if got := routeLabel(req); got != "other" {
			t.Errorf("routeLabel(%q) = %q, want \"other\"", path, got)
		}
	}
}

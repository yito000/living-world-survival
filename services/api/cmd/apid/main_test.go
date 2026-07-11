package main

import (
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestEnvOr(t *testing.T) {
	t.Setenv("API_PORT", "18082")
	if got := envOr("API_PORT", "8082"); got != "18082" {
		t.Fatalf("envOr set: got %q want %q", got, "18082")
	}
	if got := envOr("DEFINITELY_UNSET_KEY_XYZ", "fallback"); got != "fallback" {
		t.Fatalf("envOr fallback: got %q want %q", got, "fallback")
	}
}

func TestHealthz(t *testing.T) {
	mux := http.NewServeMux()
	mux.HandleFunc("/healthz", func(w http.ResponseWriter, _ *http.Request) {
		writeJSON(w, http.StatusOK, `{"status":"ok","service":"api"}`)
	})

	req := httptest.NewRequest(http.MethodGet, "/healthz", nil)
	rec := httptest.NewRecorder()
	mux.ServeHTTP(rec, req)

	if rec.Code != http.StatusOK {
		t.Fatalf("healthz status: got %d want %d", rec.Code, http.StatusOK)
	}
}

// Command authd is the M0 skeleton of the Auth / Matchmaking service.
// It exposes liveness (/healthz) and readiness (/readyz, DB ping) endpoints.
package main

import (
	"context"
	"errors"
	"log"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
)

func main() {
	addr := ":" + envOr("AUTH_PORT", "8081")
	dsn := envOr("DATABASE_URL", "postgres://survival:survival@localhost:5432/survival?sslmode=disable")

	ctx := context.Background()

	// Pool is created lazily; connectivity is verified in /readyz so that the
	// process can start even before Postgres is fully ready (health-gated deps).
	pool, err := pgxpool.New(ctx, dsn)
	if err != nil {
		log.Fatalf("authd: failed to create pgx pool: %v", err)
	}
	defer pool.Close()

	mux := http.NewServeMux()
	mux.HandleFunc("/healthz", func(w http.ResponseWriter, _ *http.Request) {
		writeJSON(w, http.StatusOK, `{"status":"ok","service":"auth"}`)
	})
	mux.HandleFunc("/readyz", func(w http.ResponseWriter, r *http.Request) {
		pingCtx, cancel := context.WithTimeout(r.Context(), 2*time.Second)
		defer cancel()
		if err := pool.Ping(pingCtx); err != nil {
			log.Printf("authd: readiness DB ping failed: %v", err)
			writeJSON(w, http.StatusServiceUnavailable, `{"status":"unavailable","dependency":"postgres"}`)
			return
		}
		writeJSON(w, http.StatusOK, `{"status":"ready","dependency":"postgres"}`)
	})

	srv := &http.Server{
		Addr:              addr,
		Handler:           mux,
		ReadHeaderTimeout: 5 * time.Second,
	}

	go func() {
		log.Printf("authd: listening on %s", addr)
		if err := srv.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
			log.Fatalf("authd: server error: %v", err)
		}
	}()

	waitForSignal()

	shutdownCtx, cancel := context.WithTimeout(ctx, 10*time.Second)
	defer cancel()
	if err := srv.Shutdown(shutdownCtx); err != nil {
		log.Printf("authd: graceful shutdown failed: %v", err)
	}
	log.Printf("authd: stopped")
}

func envOr(key, fallback string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return fallback
}

func writeJSON(w http.ResponseWriter, status int, body string) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	if _, err := w.Write([]byte(body)); err != nil {
		log.Printf("authd: write response failed: %v", err)
	}
}

func waitForSignal() {
	sig := make(chan os.Signal, 1)
	signal.Notify(sig, syscall.SIGINT, syscall.SIGTERM)
	<-sig
}

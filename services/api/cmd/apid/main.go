// Command apid is the M0 skeleton of the API / persistence service.
// It exposes liveness (/healthz) and readiness (/readyz) that verifies both
// Postgres and NATS connectivity.
package main

import (
	"context"
	"errors"
	"log"
	"net/http"
	"os"
	"os/signal"
	"sync/atomic"
	"syscall"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/nats-io/nats.go"
)

func main() {
	addr := ":" + envOr("API_PORT", "8082")
	dsn := envOr("DATABASE_URL", "postgres://survival:survival@localhost:5432/survival?sslmode=disable")
	natsURL := envOr("NATS_URL", nats.DefaultURL)

	ctx := context.Background()

	pool, err := pgxpool.New(ctx, dsn)
	if err != nil {
		log.Fatalf("apid: failed to create pgx pool: %v", err)
	}
	defer pool.Close()

	// NATS connection is established with retry in the background so the process
	// can start before NATS is ready. Readiness reflects the live status.
	var nc atomic.Pointer[nats.Conn]
	go connectNATS(natsURL, &nc)

	mux := http.NewServeMux()
	mux.HandleFunc("/healthz", func(w http.ResponseWriter, _ *http.Request) {
		writeJSON(w, http.StatusOK, `{"status":"ok","service":"api"}`)
	})
	mux.HandleFunc("/readyz", func(w http.ResponseWriter, r *http.Request) {
		pingCtx, cancel := context.WithTimeout(r.Context(), 2*time.Second)
		defer cancel()
		if err := pool.Ping(pingCtx); err != nil {
			log.Printf("apid: readiness DB ping failed: %v", err)
			writeJSON(w, http.StatusServiceUnavailable, `{"status":"unavailable","dependency":"postgres"}`)
			return
		}
		if c := nc.Load(); c == nil || !c.IsConnected() {
			writeJSON(w, http.StatusServiceUnavailable, `{"status":"unavailable","dependency":"nats"}`)
			return
		}
		writeJSON(w, http.StatusOK, `{"status":"ready","dependency":"postgres,nats"}`)
	})

	srv := &http.Server{
		Addr:              addr,
		Handler:           mux,
		ReadHeaderTimeout: 5 * time.Second,
	}

	go func() {
		log.Printf("apid: listening on %s", addr)
		if err := srv.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
			log.Fatalf("apid: server error: %v", err)
		}
	}()

	waitForSignal()

	if c := nc.Load(); c != nil {
		c.Close()
	}
	shutdownCtx, cancel := context.WithTimeout(ctx, 10*time.Second)
	defer cancel()
	if err := srv.Shutdown(shutdownCtx); err != nil {
		log.Printf("apid: graceful shutdown failed: %v", err)
	}
	log.Printf("apid: stopped")
}

// connectNATS keeps trying to connect until it succeeds, then stores the conn.
func connectNATS(url string, dst *atomic.Pointer[nats.Conn]) {
	for {
		c, err := nats.Connect(url,
			nats.RetryOnFailedConnect(true),
			nats.MaxReconnects(-1),
			nats.ReconnectWait(2*time.Second),
		)
		if err == nil {
			dst.Store(c)
			log.Printf("apid: connected to NATS at %s", url)
			return
		}
		log.Printf("apid: NATS connect failed (%v), retrying...", err)
		time.Sleep(2 * time.Second)
	}
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
		log.Printf("apid: write response failed: %v", err)
	}
}

func waitForSignal() {
	sig := make(chan os.Signal, 1)
	signal.Notify(sig, syscall.SIGINT, syscall.SIGTERM)
	<-sig
}

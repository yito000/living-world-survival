// Command authd is the Auth / Matchmaking service. It serves the Client-facing
// REST API and liveness/readiness on AUTH_PORT, and the internal
// MatchmakingService gRPC on AUTH_GRPC_PORT (M1).
package main

import (
	"context"
	"errors"
	"log"
	"net"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
	grpclib "google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/metadata"
	"google.golang.org/grpc/status"

	"living-world-survival/services/auth/internal/config"
	authgrpc "living-world-survival/services/auth/internal/grpc"
	"living-world-survival/services/auth/internal/rest"
	"living-world-survival/services/auth/internal/store"
	"living-world-survival/services/auth/internal/ticket"
	"living-world-survival/services/auth/internal/token"
	survivalv1 "living-world-survival/services/gen/go/survival/v1"
)

func main() {
	cfg, err := config.Load()
	if err != nil {
		log.Fatalf("authd: config: %v", err)
	}

	ctx := context.Background()
	pool, err := pgxpool.New(ctx, cfg.DatabaseURL)
	if err != nil {
		log.Fatalf("authd: failed to create pgx pool: %v", err)
	}
	defer pool.Close()

	st := store.New(pool)
	tokens := token.NewService(cfg.JWTSigningKey, cfg.AccessTokenTTL, cfg.RefreshTokenTTL, st)
	tickets := ticket.NewSigner(cfg.JoinTicketPrivateKey, cfg.JoinTicketPublicKey, cfg.JoinTicketTTL)

	httpSrv := newHTTPServer(cfg, pool, &rest.Server{Store: st, Tokens: tokens, Tickets: tickets})
	grpcSrv := newGRPCServer(cfg, &authgrpc.Server{Store: st, Tickets: tickets})

	// REST + health on AUTH_PORT.
	go func() {
		log.Printf("authd: REST listening on %s", cfg.HTTPAddr)
		if err := httpSrv.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
			log.Fatalf("authd: http server error: %v", err)
		}
	}()

	// Internal gRPC on AUTH_GRPC_PORT.
	grpcLis, err := net.Listen("tcp", cfg.GRPCAddr)
	if err != nil {
		log.Fatalf("authd: gRPC listen %s: %v", cfg.GRPCAddr, err)
	}
	go func() {
		log.Printf("authd: gRPC listening on %s", cfg.GRPCAddr)
		if err := grpcSrv.Serve(grpcLis); err != nil && !errors.Is(err, grpclib.ErrServerStopped) {
			log.Fatalf("authd: grpc server error: %v", err)
		}
	}()

	waitForSignal()

	shutdownCtx, cancel := context.WithTimeout(ctx, 10*time.Second)
	defer cancel()
	if err := httpSrv.Shutdown(shutdownCtx); err != nil {
		log.Printf("authd: http graceful shutdown failed: %v", err)
	}
	grpcSrv.GracefulStop()
	log.Printf("authd: stopped")
}

func newHTTPServer(cfg *config.Config, pool *pgxpool.Pool, restSrv *rest.Server) *http.Server {
	mux := http.NewServeMux()
	mux.HandleFunc("/healthz", func(w http.ResponseWriter, _ *http.Request) {
		writeRawJSON(w, http.StatusOK, `{"status":"ok","service":"auth"}`)
	})
	mux.HandleFunc("/readyz", func(w http.ResponseWriter, r *http.Request) {
		pingCtx, cancel := context.WithTimeout(r.Context(), 2*time.Second)
		defer cancel()
		if err := pool.Ping(pingCtx); err != nil {
			log.Printf("authd: readiness DB ping failed: %v", err)
			writeRawJSON(w, http.StatusServiceUnavailable, `{"status":"unavailable","dependency":"postgres"}`)
			return
		}
		writeRawJSON(w, http.StatusOK, `{"status":"ready","dependency":"postgres"}`)
	})
	// Client-facing REST (/v1/...). The inner mux matches method + full path.
	mux.Handle("/v1/", restSrv.Handler())

	return &http.Server{
		Addr:              cfg.HTTPAddr,
		Handler:           mux,
		ReadHeaderTimeout: 5 * time.Second,
	}
}

func newGRPCServer(cfg *config.Config, mm survivalv1.MatchmakingServiceServer) *grpclib.Server {
	var opts []grpclib.ServerOption
	if cfg.GRPCSharedSecret != "" {
		opts = append(opts, grpclib.UnaryInterceptor(sharedSecretInterceptor(cfg.GRPCSharedSecret)))
	}
	s := grpclib.NewServer(opts...)
	survivalv1.RegisterMatchmakingServiceServer(s, mm)
	return s
}

// sharedSecretInterceptor enforces a static service credential when configured
// (BSD第11章). This is the minimal M1 hook; production would use mTLS / mint
// short-lived service tokens.
func sharedSecretInterceptor(secret string) grpclib.UnaryServerInterceptor {
	return func(ctx context.Context, req any, _ *grpclib.UnaryServerInfo, handler grpclib.UnaryHandler) (any, error) {
		md, _ := metadata.FromIncomingContext(ctx)
		vals := md.Get("x-service-secret")
		if len(vals) == 0 || vals[0] != secret {
			return nil, status.Error(codes.Unauthenticated, "missing or invalid service credential")
		}
		return handler(ctx, req)
	}
}

func writeRawJSON(w http.ResponseWriter, status int, body string) {
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

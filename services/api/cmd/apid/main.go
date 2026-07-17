// Command apid is the API / persistence service. It serves HTTP liveness
// (/healthz) and readiness (/readyz) on API_PORT, the internal WorldData /
// ActorState gRPC on API_GRPC_PORT (M2), an outbox→NATS relay, and seeds the
// Item Definition master at startup.
package main

import (
	"context"
	"errors"
	"fmt"
	"log"
	"net"
	"net/http"
	"os"
	"os/signal"
	"sync/atomic"
	"syscall"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/nats-io/nats.go"
	grpclib "google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/metadata"
	"google.golang.org/grpc/status"

	"living-world-survival/services/api/internal/config"
	"living-world-survival/services/api/internal/economy"
	"living-world-survival/services/api/internal/grpcserver"
	"living-world-survival/services/api/internal/itemdef"
	"living-world-survival/services/api/internal/outbox"
	"living-world-survival/services/api/internal/ranking"
	"living-world-survival/services/api/internal/store"
	"living-world-survival/services/api/internal/worldevent"
	survivalv1 "living-world-survival/services/gen/go/survival/v1"
)

func main() {
	cfg := config.Load()

	// Item Definition master is a deterministic config file; fail fast if it is
	// missing or invalid (3.8 / DoD).
	catalog, err := itemdef.Load(cfg.ItemDefPath)
	if err != nil {
		log.Fatalf("apid: item definitions: %v", err)
	}
	log.Printf("apid: loaded %d item definitions from %s", catalog.Len(), cfg.ItemDefPath)

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	pool, err := pgxpool.New(ctx, cfg.DatabaseURL)
	if err != nil {
		log.Fatalf("apid: failed to create pgx pool: %v", err)
	}
	defer pool.Close()

	st := store.New(pool)

	// Seed the Item Definition master into Postgres with retry (DB may not be
	// ready yet). Non-fatal: the RPC surface does not depend on the DB seed.
	go seedItemDefinitions(ctx, catalog, st)

	// The WorldEventService announces Completed on worldevent.result, so it needs
	// the NATS connection that is established asynchronously below. The publisher
	// is swapped in once connected; until then transitions still commit, they are
	// just not announced (3.6).
	weServer := &worldevent.Server{Store: st}

	// EconomyService owns the purchase/sale commits and Buyer stock (M6). Its
	// Buyer Stock Definitions are embedded and validated at load, so a broken
	// table is a startup failure rather than a failed purchase at runtime.
	economyServer, err := economy.NewServer(st, catalog)
	if err != nil {
		log.Fatalf("apid: economy: %v", err)
	}

	// The asset ranking batch is heavy, so it runs on its own goroutine and never
	// inside a request handler (MVP 12.3 / 09B 3.9).
	rankingBatch := ranking.New(st)
	go rankingBatch.RunPeriodically(ctx, cfg.RankingInterval)

	// Connect to NATS in the background, then start the outbox relay and the
	// world event proposal approver.
	var nc atomic.Pointer[nats.Conn]
	go func() {
		connectNATS(ctx, cfg.NATSURL, &nc)
		c := nc.Load()
		if c == nil {
			return
		}
		startWorldEventApprover(ctx, c, st, weServer)
		startRelay(ctx, c, st, cfg)
	}()

	httpSrv := newHTTPServer(cfg, pool, &nc, rankingBatch)
	grpcSrv := newGRPCServer(cfg, st, weServer, economyServer)

	go func() {
		log.Printf("apid: HTTP listening on %s", cfg.HTTPAddr)
		if err := httpSrv.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
			log.Fatalf("apid: http server error: %v", err)
		}
	}()

	grpcLis, err := net.Listen("tcp", cfg.GRPCAddr)
	if err != nil {
		log.Fatalf("apid: gRPC listen %s: %v", cfg.GRPCAddr, err)
	}
	go func() {
		log.Printf("apid: gRPC listening on %s", cfg.GRPCAddr)
		if err := grpcSrv.Serve(grpcLis); err != nil && !errors.Is(err, grpclib.ErrServerStopped) {
			log.Fatalf("apid: grpc server error: %v", err)
		}
	}()

	waitForSignal()
	cancel() // stop background workers (relay, seeding)

	if c := nc.Load(); c != nil {
		c.Close()
	}
	shutdownCtx, shutdownCancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer shutdownCancel()
	if err := httpSrv.Shutdown(shutdownCtx); err != nil {
		log.Printf("apid: http graceful shutdown failed: %v", err)
	}
	grpcSrv.GracefulStop()
	log.Printf("apid: stopped")
}

func newHTTPServer(cfg *config.Config, pool *pgxpool.Pool, nc *atomic.Pointer[nats.Conn], rankingBatch *ranking.Batch) *http.Server {
	mux := http.NewServeMux()
	mux.HandleFunc("/healthz", func(w http.ResponseWriter, _ *http.Request) {
		writeJSON(w, http.StatusOK, `{"status":"ok","service":"api"}`)
	})
	mux.HandleFunc("/admin/ranking/run", adminRankingHandler(rankingBatch))
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
	return &http.Server{
		Addr:              cfg.HTTPAddr,
		Handler:           mux,
		ReadHeaderTimeout: 5 * time.Second,
	}
}

func newGRPCServer(cfg *config.Config, st *store.Store, we *worldevent.Server, ec *economy.Server) *grpclib.Server {
	var opts []grpclib.ServerOption
	if cfg.GRPCSharedSecret != "" {
		opts = append(opts, grpclib.UnaryInterceptor(sharedSecretInterceptor(cfg.GRPCSharedSecret)))
	}
	s := grpclib.NewServer(opts...)
	survivalv1.RegisterWorldDataServiceServer(s, &grpcserver.WorldDataServer{Store: st})
	survivalv1.RegisterActorStateServiceServer(s, &grpcserver.ActorStateServer{Store: st})
	survivalv1.RegisterWorldEventServiceServer(s, we)
	survivalv1.RegisterEconomyServiceServer(s, ec)
	return s
}

// adminRankingHandler triggers one ranking run on demand (09B 3.9 管理コマンド).
// It is an internal-only endpoint (MVP-SEC-001): the API's HTTP port is not
// exposed outside the internal network.
func adminRankingHandler(b *ranking.Batch) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost {
			writeJSON(w, http.StatusMethodNotAllowed, `{"error":"POST required"}`)
			return
		}
		// Detach from the request: the run must survive the client hanging up, and
		// a heavy run must not be cancelled halfway through writing a generation.
		runCtx, cancel := context.WithTimeout(context.Background(), 5*time.Minute)
		defer cancel()

		res, err := b.Run(runCtx)
		if errors.Is(err, ranking.ErrAlreadyRunning) {
			writeJSON(w, http.StatusConflict, `{"status":"already_running"}`)
			return
		}
		if err != nil {
			log.Printf("apid: ranking run failed: %v", err)
			writeJSON(w, http.StatusInternalServerError, `{"status":"failed"}`)
			return
		}
		writeJSON(w, http.StatusOK, fmt.Sprintf(
			`{"status":"ok","price_version":%d,"owners":%d}`, res.PriceVersion, res.OwnerCount))
	}
}

// startWorldEventApprover subscribes to worldevent.proposal.* and gives the
// WorldEventService its result publisher (3.6). Failure is non-fatal: Register /
// UpdateState keep working, proposals just go undecided until NATS recovers.
func startWorldEventApprover(ctx context.Context, nc *nats.Conn, st *store.Store, we *worldevent.Server) {
	results := worldevent.NewResultPublisher(nc)
	we.SetResults(results)
	approver := worldevent.NewApprover(nc, st, results)
	if err := approver.Start(ctx); err != nil {
		log.Printf("apid: world event approver disabled: %v", err)
		return
	}
	go func() {
		<-ctx.Done()
		approver.Stop()
	}()
}

func startRelay(ctx context.Context, nc *nats.Conn, st *store.Store, cfg *config.Config) {
	pub, err := outbox.NewJetStreamPublisher(nc)
	if err != nil {
		log.Printf("apid: outbox relay disabled: %v", err)
		return
	}
	relay := outbox.NewRelay(st, pub, cfg.OutboxInterval, 100)
	log.Printf("apid: outbox relay started (interval=%s)", cfg.OutboxInterval)
	relay.Run(ctx)
}

func seedItemDefinitions(ctx context.Context, catalog *itemdef.Catalog, st *store.Store) {
	for {
		if err := catalog.Seed(ctx, st); err != nil {
			log.Printf("apid: item definition seed failed (%v), retrying...", err)
			select {
			case <-ctx.Done():
				return
			case <-time.After(3 * time.Second):
				continue
			}
		}
		log.Printf("apid: seeded %d item definitions", catalog.Len())
		return
	}
}

// sharedSecretInterceptor enforces a static service credential when configured
// (MVP-SEC-007). Minimal M2 hook; production would use mTLS.
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

// connectNATS keeps trying to connect until it succeeds or ctx is cancelled.
func connectNATS(ctx context.Context, url string, dst *atomic.Pointer[nats.Conn]) {
	for {
		if ctx.Err() != nil {
			return
		}
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
		select {
		case <-ctx.Done():
			return
		case <-time.After(2 * time.Second):
		}
	}
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

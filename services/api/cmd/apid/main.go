// Command apid is the API / persistence service. It serves HTTP liveness
// (/healthz) and readiness (/readyz) on API_PORT, the internal WorldData /
// ActorState gRPC on API_GRPC_PORT (M2), an outbox→NATS relay, and seeds the
// Item Definition master at startup.
package main

import (
	"context"
	"errors"
	"flag"
	"net"
	"net/http"
	"os"
	"os/signal"
	"strings"
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
	"living-world-survival/services/api/internal/metrics"
	"living-world-survival/services/api/internal/outbox"
	"living-world-survival/services/api/internal/ranking"
	"living-world-survival/services/api/internal/store"
	"living-world-survival/services/api/internal/worldevent"
	"living-world-survival/services/common/obs"
	survivalv1 "living-world-survival/services/gen/go/survival/v1"
)

// healthcheckFlag は Docker の HEALTHCHECK 用の自己診断モード（3.8）。
// 実行イメージは distroless/static で shell も curl も無いため、バイナリ自身が
// 自分の /readyz を叩いて 200 なら 0、それ以外なら 1 で終了する。
var healthcheckFlag = flag.Bool("healthcheck", false,
	"probe this service's own /readyz and exit 0 (ready) / 1 (not ready)")

func main() {
	flag.Parse()
	// healthcheck は DB/NATS のセットアップより前に完結させる。ここより後ろに
	// 置くと healthcheck プロセスが interval ごとに pgx pool を開き、接続数を
	// 食い潰す。
	if *healthcheckFlag {
		os.Exit(runHealthcheck(config.Load().HTTPAddr))
	}

	log := obs.Init("api")
	cfg := config.Load()

	// Item Definition master is a deterministic config file; fail fast if it is
	// missing or invalid (3.8 / DoD).
	catalog, err := itemdef.Load(cfg.ItemDefPath)
	if err != nil {
		log.Error("item definitions", "error", err.Error())
		os.Exit(1)
	}
	log.Info("loaded item definitions", "count", catalog.Len(), "path", cfg.ItemDefPath)

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	pool, err := newPool(ctx, cfg.DatabaseURL)
	if err != nil {
		log.Error("failed to create pgx pool", "error", err.Error())
		os.Exit(1)
	}
	defer pool.Close()

	st := store.New(pool)

	// Outbox depth / event lag は一定間隔でサンプリングして Gauge へ載せる
	// （10B 3.2 / 3.5）。スクレイプのたびに DB を叩かない。
	go metrics.NewSampler(st, cfg.MetricsSampleInterval).Run(ctx)

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
		log.Error("economy", "error", err.Error())
		os.Exit(1)
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
		log.Info("HTTP listening", "addr", cfg.HTTPAddr)
		if err := httpSrv.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
			log.Error("http server error", "error", err.Error())
			os.Exit(1)
		}
	}()

	grpcLis, err := net.Listen("tcp", cfg.GRPCAddr)
	if err != nil {
		log.Error("gRPC listen failed", "addr", cfg.GRPCAddr, "error", err.Error())
		os.Exit(1)
	}
	go func() {
		log.Info("gRPC listening", "addr", cfg.GRPCAddr)
		if err := grpcSrv.Serve(grpcLis); err != nil && !errors.Is(err, grpclib.ErrServerStopped) {
			log.Error("grpc server error", "error", err.Error())
			os.Exit(1)
		}
	}()

	waitForSignal()
	cancel() // stop background workers (relay, seeding, sampler)

	if c := nc.Load(); c != nil {
		c.Close()
	}
	shutdownCtx, shutdownCancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer shutdownCancel()
	if err := httpSrv.Shutdown(shutdownCtx); err != nil {
		log.Warn("http graceful shutdown failed", "error", err.Error())
	}
	grpcSrv.GracefulStop()
	log.Info("stopped")
}

// runHealthcheck GETs /readyz on this process's own listen port and returns the
// exit code Docker's HEALTHCHECK expects（200=0 / それ以外=1）。
// /readyz は依存断（DB/NATS）で 503 を返すので、その間コンテナは unhealthy に
// なる。RC ではそれが意図（トラフィックを止める）で、再起動の引き金にはしない。
func runHealthcheck(httpAddr string) int {
	port := httpAddr
	if _, p, err := net.SplitHostPort(httpAddr); err == nil {
		port = p
	}
	port = strings.TrimPrefix(port, ":")

	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, "http://127.0.0.1:"+port+"/readyz", nil)
	if err != nil {
		return 1
	}
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		return 1
	}
	defer func() { _ = resp.Body.Close() }()
	if resp.StatusCode != http.StatusOK {
		return 1
	}
	return 0
}

// newPool は DB プールを作り、全クエリの所要時間を metrics へ記録する
// QueryTracer を挿す（第13章 DB latency）。各 store メソッドに計測を撒くと
// 撒き忘れた経路が黙って計測から漏れるので、pool へ 1 箇所で入れる。
func newPool(ctx context.Context, dsn string) (*pgxpool.Pool, error) {
	cfg, err := pgxpool.ParseConfig(dsn)
	if err != nil {
		return nil, err
	}
	cfg.ConnConfig.Tracer = obs.QueryTracer{}
	return pgxpool.NewWithConfig(ctx, cfg)
}

// knownRoutes は metrics の route ラベルに出してよいパス（未知パスで
// 時系列が増え続けるのを防ぐ）。
var knownRoutes = map[string]bool{
	"/healthz": true, "/readyz": true, "/metrics": true, "/admin/ranking/run": true,
}

func routeLabel(r *http.Request) string {
	if knownRoutes[r.URL.Path] {
		return r.URL.Path
	}
	return "other"
}

func newHTTPServer(cfg *config.Config, pool *pgxpool.Pool, nc *atomic.Pointer[nats.Conn], rankingBatch *ranking.Batch) *http.Server {
	mux := http.NewServeMux()
	mux.HandleFunc("/healthz", obs.LivenessHandler("api"))
	mux.HandleFunc("/admin/ranking/run", adminRankingHandler(rankingBatch))
	mux.HandleFunc("/readyz", obs.ReadinessHandler(
		obs.Check{Name: "postgres", Probe: pool.Ping},
		obs.Check{Name: "nats", Probe: func(context.Context) error {
			if c := nc.Load(); c == nil || !c.IsConnected() {
				return errors.New("not connected")
			}
			return nil
		}},
	))
	// /metrics は負荷/Soak ハーネスがスクレイプする（10B 3.1/3.2）。
	mux.Handle("/metrics", obs.MetricsHandler())
	return &http.Server{
		Addr:              cfg.HTTPAddr,
		Handler:           obs.Middleware(routeLabel)(mux),
		ReadHeaderTimeout: 5 * time.Second,
	}
}

func newGRPCServer(cfg *config.Config, st *store.Store, we *worldevent.Server, ec *economy.Server) *grpclib.Server {
	// 相関 ID/計測は常に、共有シークレット検証は設定時のみ。認証を先に置くと
	// 未認証リクエストが計測から漏れる。
	interceptors := []grpclib.UnaryServerInterceptor{obs.UnaryServerInterceptor()}
	if cfg.GRPCSharedSecret != "" {
		interceptors = append(interceptors, sharedSecretInterceptor(cfg.GRPCSharedSecret))
	}
	s := grpclib.NewServer(grpclib.ChainUnaryInterceptor(interceptors...))
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
			obs.WriteJSON(w, http.StatusMethodNotAllowed, map[string]string{"error": "POST required"})
			return
		}
		// Detach from the request: the run must survive the client hanging up, and
		// a heavy run must not be cancelled halfway through writing a generation.
		runCtx, cancel := context.WithTimeout(context.Background(), 5*time.Minute)
		defer cancel()

		res, err := b.Run(runCtx)
		if errors.Is(err, ranking.ErrAlreadyRunning) {
			metrics.RankingRuns.WithLabelValues("already_running").Inc()
			obs.WriteJSON(w, http.StatusConflict, map[string]string{"status": "already_running"})
			return
		}
		if err != nil {
			metrics.RankingRuns.WithLabelValues("failed").Inc()
			obs.L(r.Context()).Error("ranking run failed", "error", err.Error())
			obs.WriteJSON(w, http.StatusInternalServerError, map[string]string{"status": "failed"})
			return
		}
		metrics.RankingRuns.WithLabelValues("ok").Inc()
		obs.WriteJSON(w, http.StatusOK, map[string]any{
			"status": "ok", "price_version": res.PriceVersion, "owners": res.OwnerCount,
		})
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
		obs.L(ctx).Warn("world event approver disabled", "error", err.Error())
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
		obs.L(ctx).Warn("outbox relay disabled", "error", err.Error())
		return
	}
	relay := outbox.NewRelay(st, pub, cfg.OutboxInterval, 100)
	obs.L(ctx).Info("outbox relay started", "interval", cfg.OutboxInterval.String())
	relay.Run(ctx)
}

func seedItemDefinitions(ctx context.Context, catalog *itemdef.Catalog, st *store.Store) {
	for {
		if err := catalog.Seed(ctx, st); err != nil {
			obs.L(ctx).Warn("item definition seed failed, retrying", "error", err.Error())
			select {
			case <-ctx.Done():
				return
			case <-time.After(3 * time.Second):
				continue
			}
		}
		obs.L(ctx).Info("seeded item definitions", "count", catalog.Len())
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
			obs.L(ctx).Info("connected to NATS", "url", url)
			return
		}
		obs.L(ctx).Warn("NATS connect failed, retrying", "error", err.Error())
		select {
		case <-ctx.Done():
			return
		case <-time.After(2 * time.Second):
		}
	}
}

func waitForSignal() {
	sig := make(chan os.Signal, 1)
	signal.Notify(sig, syscall.SIGINT, syscall.SIGTERM)
	<-sig
}

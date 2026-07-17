// Command authd is the Auth / Matchmaking service. It serves the Client-facing
// REST API, liveness/readiness and Prometheus metrics on AUTH_PORT, and the
// internal MatchmakingService gRPC on AUTH_GRPC_PORT (M1).
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
	"living-world-survival/services/common/obs"
	"living-world-survival/services/common/ratelimit"
	survivalv1 "living-world-survival/services/gen/go/survival/v1"
)

// healthcheckFlag は Docker の HEALTHCHECK 用の自己診断モード（3.8）。
// 実行イメージは distroless/static で shell も curl も無いため、バイナリ自身が
// 自分の /readyz を叩いて 200 なら 0、それ以外なら 1 で終了する。
var healthcheckFlag = flag.Bool("healthcheck", false,
	"probe this service's own /readyz and exit 0 (ready) / 1 (not ready)")

func main() {
	flag.Parse()
	// healthcheck は DB 接続より前に完結させる。ここより後ろに置くと
	// healthcheck プロセスが interval ごとに pgx pool を開き、接続数を食い潰す。
	if *healthcheckFlag {
		os.Exit(runHealthcheck(healthcheckAddr()))
	}

	log := obs.Init("auth")

	cfg, err := config.Load()
	if err != nil {
		log.Error("config", "error", err.Error())
		os.Exit(1)
	}

	ctx := context.Background()
	pool, err := newPool(ctx, cfg.DatabaseURL)
	if err != nil {
		log.Error("failed to create pgx pool", "error", err.Error())
		os.Exit(1)
	}
	defer pool.Close()

	st := store.New(pool)
	tokens := token.NewService(cfg.JWTSigningKey, cfg.AccessTokenTTL, cfg.RefreshTokenTTL, st)
	tickets := ticket.NewSigner(cfg.JoinTicketPrivateKey, cfg.JoinTicketPublicKey, cfg.JoinTicketTTL)

	restSrv := &rest.Server{
		Store: st, Tokens: tokens, Tickets: tickets,
		// Rate Limit（第16章 / MVP-SEC-005）。閾値は Config Default（3.4）。
		LoginLimiter: ratelimit.New(cfg.LoginRate, cfg.LoginRateWindow, cfg.LoginRateBurst),
		AccountLimiter: ratelimit.New(
			cfg.AccountCreateRate, cfg.AccountCreateWindow, cfg.AccountCreateRate),
	}
	log.Info("rate limits configured",
		"login_rate", cfg.LoginRate, "login_window", cfg.LoginRateWindow.String(),
		"account_create_rate", cfg.AccountCreateRate)

	httpSrv := newHTTPServer(cfg, pool, restSrv)
	grpcSrv := newGRPCServer(cfg, &authgrpc.Server{Store: st, Tickets: tickets})

	// REST + health + metrics on AUTH_PORT.
	go func() {
		log.Info("REST listening", "addr", cfg.HTTPAddr)
		if err := httpSrv.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
			log.Error("http server error", "error", err.Error())
			os.Exit(1)
		}
	}()

	// Internal gRPC on AUTH_GRPC_PORT.
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

	shutdownCtx, cancel := context.WithTimeout(ctx, 10*time.Second)
	defer cancel()
	if err := httpSrv.Shutdown(shutdownCtx); err != nil {
		log.Warn("http graceful shutdown failed", "error", err.Error())
	}
	grpcSrv.GracefulStop()
	log.Info("stopped")
}

// healthcheckAddr は healthcheck が叩く待受アドレスを返す。config.Load() は
// secret 未設定でエラーになるが healthcheck 自体は secret を必要としないため、
// その場合はサーバ本体と同じ既定で AUTH_PORT だけを解決する。
func healthcheckAddr() string {
	if cfg, err := config.Load(); err == nil {
		return cfg.HTTPAddr
	}
	port := strings.TrimSpace(os.Getenv("AUTH_PORT"))
	if port == "" {
		port = "8081"
	}
	return ":" + port
}

// runHealthcheck GETs /readyz on this process's own listen port and returns the
// exit code Docker's HEALTHCHECK expects（200=0 / それ以外=1）。
// /readyz は依存断（DB）で 503 を返すので、その間コンテナは unhealthy になる。
// RC ではそれが意図（トラフィックを止める）で、再起動の引き金にはしない。
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

// knownRoutes は metrics の route ラベルに出してよいパス。未知のパスを
// そのまま label にすると、スキャン等の任意パスで時系列が無限に増える。
var knownRoutes = map[string]bool{
	"/healthz": true, "/readyz": true, "/metrics": true,
	"/v1/accounts": true, "/v1/sessions": true, "/v1/sessions/refresh": true,
	"/v1/sessions/current": true, "/v1/matchmaking/join": true,
}

func routeLabel(r *http.Request) string {
	if knownRoutes[r.URL.Path] {
		return r.URL.Path
	}
	return "other"
}

func newHTTPServer(cfg *config.Config, pool *pgxpool.Pool, restSrv *rest.Server) *http.Server {
	mux := http.NewServeMux()
	mux.HandleFunc("/healthz", obs.LivenessHandler("auth"))
	mux.HandleFunc("/readyz", obs.ReadinessHandler(obs.Check{
		Name:  "postgres",
		Probe: pool.Ping,
	}))
	// /metrics は負荷/Soak ハーネスがスクレイプする（10B 3.1/3.2）。
	mux.Handle("/metrics", obs.MetricsHandler())
	// Client-facing REST (/v1/...). The inner mux matches method + full path.
	mux.Handle("/v1/", restSrv.Handler())

	return &http.Server{
		Addr:              cfg.HTTPAddr,
		Handler:           obs.Middleware(routeLabel)(mux),
		ReadHeaderTimeout: 5 * time.Second,
	}
}

func newGRPCServer(cfg *config.Config, mm survivalv1.MatchmakingServiceServer) *grpclib.Server {
	// 相関 ID/計測は常に、共有シークレット検証は設定時のみ。順序が重要で、
	// 認証を先に置くと未認証リクエストが計測されない。
	interceptors := []grpclib.UnaryServerInterceptor{obs.UnaryServerInterceptor()}
	if cfg.GRPCSharedSecret != "" {
		interceptors = append(interceptors, sharedSecretInterceptor(cfg.GRPCSharedSecret))
	}
	s := grpclib.NewServer(grpclib.ChainUnaryInterceptor(interceptors...))
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

func waitForSignal() {
	sig := make(chan os.Signal, 1)
	signal.Notify(sig, syscall.SIGINT, syscall.SIGTERM)
	<-sig
}

// Command loadgen は M7 負荷試験ハーネス（10B 3.1 / AT-020）の負荷投入ドライバである。
//
// # 何を計測するドライバなのか（重要 / スコープ）
//
// 本ドライバが計測するのは **バックエンド**（auth / api / PostgreSQL / NATS）である。
// 10B 0.1 の分担表で WSL2 側に割り当てられているのは「負荷試験ハーネス（バックエンド計測）」
// であり、0.2 は「負荷/Soak の Unity 側は 10A が担当し、本書はバックエンド計測とハーネス駆動に
// 限定する」と明記している。
//
// したがって loadgen は **「DS 模擬 + N プレイヤー」** の役を演じ、実在するバックエンド RPC
// 面（Auth REST / Matchmaking gRPC / WorldData gRPC / Economy gRPC）を DS 相当の負荷で駆動する。
// DS は Unity/FishNet なので、Client→DS の FishNet プロトコルを Go から喋ることはできない。
// **Client→DS の描画/同期負荷は 10A の PlayMode テストの担当であり、本ドライバの対象外**。
// ここを取り違えて「loadgen が Client 権威経路を通している」と読まないこと。
//
// # Client 権威を持たせない（MVP-SEC-005/006 / 10B 6章）
//
// 本ドライバは Damage / Loot / Drop / Craft Result / 購入価格を **一切決めない**。
//   - 購入は EconomyService が価格権威（在庫生成時に unit_price 確定 / 購入時に再計算しない）。
//     ドライバは CommitPurchase を投げ、サーバーが課金した額をそのまま受け取るだけである。
//   - 採取/使用/破棄は DS が生成する Domain Event の形（06A/06B 0.3）を模した入力を送るだけで、
//     在庫への反映は API が単一 Writer として行う。
//
// # Tick の出所（10B 6章）
//
// Gate 判定に使う tick_ms は **DS が Heartbeat で自報告した値**（auth の ds_tick_seconds）が正。
// ドライバ側の RTT を tick_ms と混同しない。実 DS を動かさない場合、本ドライバは TICK_MS で
// 与えた **合成値** を Heartbeat に載せる（＝計測値ではない）。実 DS 併走時は TICK_MS=0 を渡すと
// Heartbeat は tick_ms を載せず（auth 側が tick_ms<=0 を捨てる）、実 DS の tick だけが分布に載る。
// この区別は scripts/load_test.sh が report の tick_source に必ず記録する。
//
// # 出力
//
// ログは stderr、最後に JSON サマリ（種別ごとの attempted/succeeded/failed とクライアント側
// レイテンシ）を stdout へ 1 行で出す。**クライアント側レイテンシは参考値であり Gate ではない**。
// Gate はサーバー側メトリクス（grpc_server_handling_seconds / ds_tick_seconds）で判定する。
//
// # 主な環境変数
//
//	PLAYERS       同時プレイヤー数（既定 2。ローカル開発スケール = PLAYERS=2 AI=20）
//	AI            AI アクター数（既定 20。AT-020 目標スケール = PLAYERS=16 AI=20 ANIMALS=80）
//	ANIMALS       動物数（既定 0）
//	DURATION      負荷投入時間（既定 60s）
//	TICK_MS       合成 DS の自報告 tick_ms（既定 33。0 で Heartbeat から省略＝実 DS 併走時）
//	TICK_JITTER_MS 合成 tick のゆらぎ幅（既定 8）
//	LOADGEN_HOST  接続先ホスト（既定 localhost）
//	AUTH_PORT / AUTH_GRPC_PORT / API_GRPC_PORT
//	DATABASE_URL_HOST  world/残高の下拵え用（DS/運営が行う初期化の代役）
//	AUTH_GRPC_SHARED_SECRET / API_GRPC_SHARED_SECRET  設定時は x-service-secret を付与
//	LOADGEN_BUILD_ID   合成 DS の build_id（実 DS と別値にして Matchmaking の取り違えを防ぐ）
package main

import (
	"context"
	"encoding/json"
	"fmt"
	"log"
	"os"
	"os/signal"
	"sync"
	"syscall"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials/insecure"

	survivalv1 "living-world-survival/services/gen/go/survival/v1"
)

// config は規模と接続先。すべて環境変数で可変（10B 3.1）。
type config struct {
	Players  int
	AI       int
	Animals  int
	Duration time.Duration

	TickMS       int
	TickJitterMS int

	RESTBase string
	AuthGRPC string
	APIGRPC  string
	DBURL    string

	AuthSecret string
	APISecret  string
	BuildID    string

	PlayerOpInterval time.Duration
	AIOpInterval     time.Duration
	AnimalOpInterval time.Duration
}

func loadConfig() config {
	host := envOr("LOADGEN_HOST", "localhost")
	return config{
		Players:  envInt("PLAYERS", 2),
		AI:       envInt("AI", 20),
		Animals:  envInt("ANIMALS", 0),
		Duration: envDur("DURATION", 60*time.Second),

		TickMS:       envInt("TICK_MS", 33),
		TickJitterMS: envInt("TICK_JITTER_MS", 8),

		RESTBase: fmt.Sprintf("http://%s:%s", host, envOr("AUTH_PORT", "8081")),
		AuthGRPC: fmt.Sprintf("%s:%s", host, envOr("AUTH_GRPC_PORT", "9091")),
		APIGRPC:  fmt.Sprintf("%s:%s", host, envOr("API_GRPC_PORT", "8092")),
		DBURL: envOr("DATABASE_URL_HOST",
			"postgres://survival:survival@localhost:5432/survival?sslmode=disable"),

		AuthSecret: os.Getenv("AUTH_GRPC_SHARED_SECRET"),
		APISecret:  os.Getenv("API_GRPC_SHARED_SECRET"),
		BuildID:    envOr("LOADGEN_BUILD_ID", "loadgen-build"),

		PlayerOpInterval: envDur("PLAYER_OP_INTERVAL", 200*time.Millisecond),
		AIOpInterval:     envDur("AI_OP_INTERVAL", 1*time.Second),
		AnimalOpInterval: envDur("ANIMAL_OP_INTERVAL", 3*time.Second),
	}
}

// 初期残高。購入 Gate（P95≤500ms）を測る間に残高切れで INSUFFICIENT_FUNDS へ落ちると
// 「DB commit を含む購入」の分布が測れないため、十分に大きい整数を積む（通貨は int64 最小単位）。
const startingBalance int64 = 1_000_000_000

func main() {
	log.SetFlags(log.Ltime)
	log.SetOutput(os.Stderr) // stdout は JSON サマリ専用。

	cfg := loadConfig()
	log.Printf("loadgen: scale players=%d ai=%d animals=%d duration=%s tick_ms=%d",
		cfg.Players, cfg.AI, cfg.Animals, cfg.Duration, cfg.TickMS)

	// SIGTERM/SIGINT で clean shutdown（MarkDraining まで到達させる）。
	ctx, stop := signal.NotifyContext(context.Background(), syscall.SIGINT, syscall.SIGTERM)
	defer stop()

	pool, err := pgxpool.New(ctx, cfg.DBURL)
	if err != nil {
		fatal("postgres 接続: %v", err)
	}
	defer pool.Close()

	authConn, err := grpc.NewClient(cfg.AuthGRPC, grpc.WithTransportCredentials(insecure.NewCredentials()))
	if err != nil {
		fatal("auth gRPC dial %s: %v", cfg.AuthGRPC, err)
	}
	defer func() { _ = authConn.Close() }()

	apiConn, err := grpc.NewClient(cfg.APIGRPC, grpc.WithTransportCredentials(insecure.NewCredentials()))
	if err != nil {
		fatal("api gRPC dial %s: %v", cfg.APIGRPC, err)
	}
	defer func() { _ = apiConn.Close() }()

	mm := survivalv1.NewMatchmakingServiceClient(authConn)
	wd := survivalv1.NewWorldDataServiceClient(apiConn)
	econ := survivalv1.NewEconomyServiceClient(apiConn)

	stats := newStats()

	// 世界を用意する。実運用では世界初期化が作る行で、ドライバの計測対象ではない。
	worldID := newUUID()
	setupCtx, setupCancel := context.WithTimeout(ctx, 30*time.Second)
	defer setupCancel()
	if _, err := pool.Exec(setupCtx, `INSERT INTO worlds (world_id) VALUES ($1)`, worldID); err != nil {
		fatal("world 作成: %v", err)
	}
	log.Printf("loadgen: world=%s", worldID)

	// 合成 DS を登録して ready にする（Matchmaking の join 先になる）。
	ds := &syntheticDS{
		mm:       mm,
		secret:   cfg.AuthSecret,
		serverID: newUUID(),
		worldID:  worldID,
		buildID:  cfg.BuildID,
		tickMS:   cfg.TickMS,
		jitterMS: cfg.TickJitterMS,
		players:  cfg.Players,
		stats:    stats,
	}
	if err := ds.register(setupCtx); err != nil {
		fatal("RegisterServer: %v", err)
	}
	log.Printf("loadgen: synthetic DS registered server_id=%s build_id=%s", ds.serverID, ds.buildID)

	// 負荷投入の時間窓。DURATION 経過か SIGTERM で終わる。
	runCtx, runCancel := context.WithTimeout(ctx, cfg.Duration)
	defer runCancel()

	startedAt := time.Now()
	var wg sync.WaitGroup

	// 合成 DS Heartbeat（約 1Hz）。
	wg.Add(1)
	go func() {
		defer wg.Done()
		ds.heartbeatLoop(runCtx)
	}()

	// プレイヤー: goroutine 1 本ずつ。auth フロー → gameplay ループ。
	for i := 0; i < cfg.Players; i++ {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			a := &actor{
				kind:    "player",
				name:    fmt.Sprintf("player-%d", i),
				worldID: worldID,
				actorID: newUUID(),
				// 購入者 id は gameplay の actor と別にする。CommitPurchase は
				// inventory_version の一致を要求するが、採取/使用は同じ inventory の
				// version を非同期に上げてしまい、購入が version 不一致で REJECTED に
				// 落ちて購入 Gate が測れなくなるため。
				buyerFor: newUUID(),
				wd:       wd,
				econ:     econ,
				pool:     pool,
				secret:   cfg.APISecret,
				stats:    stats,
				interval: cfg.PlayerOpInterval,
				buys:     true,
			}
			if err := a.seedBalance(runCtx); err != nil {
				log.Printf("loadgen: %s: 残高投入失敗: %v", a.name, err)
				return
			}
			// プレイヤーだけが auth REST + Matchmaking を通る（AI/動物は DS 内実体なので通らない）。
			p := &playerSession{cfg: cfg, ds: ds, stats: stats, name: a.name}
			if err := p.join(runCtx); err != nil {
				log.Printf("loadgen: %s: join 失敗: %v", a.name, err)
				return
			}
			a.run(runCtx)
		}(i)
	}

	// AI アクター: DS 内実体なので auth は通らない。gameplay イベントと購入だけを出す（M4/M6）。
	for i := 0; i < cfg.AI; i++ {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			a := &actor{
				kind:     "ai",
				name:     fmt.Sprintf("ai-%d", i),
				worldID:  worldID,
				actorID:  newUUID(),
				buyerFor: newUUID(),
				wd:       wd,
				econ:     econ,
				pool:     pool,
				secret:   cfg.APISecret,
				stats:    stats,
				interval: cfg.AIOpInterval,
				buys:     true,
			}
			if err := a.seedBalance(runCtx); err != nil {
				log.Printf("loadgen: %s: 残高投入失敗: %v", a.name, err)
				return
			}
			a.run(runCtx)
		}(i)
	}

	// 動物: バックエンドから見た負荷は狩猟イベントの発生分だけ（本体は DS 側の負荷 = 10A 担当）。
	for i := 0; i < cfg.Animals; i++ {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			a := &actor{
				kind:     "animal",
				name:     fmt.Sprintf("animal-%d", i),
				worldID:  worldID,
				actorID:  newUUID(),
				wd:       wd,
				pool:     pool,
				secret:   cfg.APISecret,
				stats:    stats,
				interval: cfg.AnimalOpInterval,
			}
			a.runAnimal(runCtx)
		}(i)
	}

	wg.Wait()
	finishedAt := time.Now()

	// 撤収: Matchmaking から外す（G4）。runCtx は期限切れなので別 ctx を使う。
	drainCtx, drainCancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer drainCancel()
	if err := ds.markDraining(drainCtx); err != nil {
		log.Printf("loadgen: MarkDraining 失敗: %v", err)
	} else {
		log.Printf("loadgen: synthetic DS drained")
	}

	out := summary{
		Schema:   "loadgen_summary/v1",
		WorldID:  worldID,
		ServerID: ds.serverID,
		Scale: scaleInfo{
			Players: cfg.Players, AI: cfg.AI, Animals: cfg.Animals,
			Duration: cfg.Duration.String(),
		},
		SyntheticTickMS: cfg.TickMS,
		StartedAt:       startedAt.UTC().Format(time.RFC3339Nano),
		FinishedAt:      finishedAt.UTC().Format(time.RFC3339Nano),
		ElapsedSeconds:  int64(finishedAt.Sub(startedAt) / time.Millisecond),
		Ops:             stats.snapshot(),
		Note: "client_latency_ms は参考値。Gate はサーバー側メトリクス" +
			"（grpc_server_handling_seconds / ds_tick_seconds）で判定する（10B 6章）。",
	}
	enc := json.NewEncoder(os.Stdout)
	enc.SetIndent("", "  ")
	if err := enc.Encode(out); err != nil {
		fatal("サマリ出力: %v", err)
	}
}

type scaleInfo struct {
	Players  int    `json:"players"`
	AI       int    `json:"ai"`
	Animals  int    `json:"animals"`
	Duration string `json:"duration"`
}

type summary struct {
	Schema          string              `json:"schema"`
	WorldID         string              `json:"world_id"`
	ServerID        string              `json:"server_id"`
	Scale           scaleInfo           `json:"scale"`
	SyntheticTickMS int                 `json:"synthetic_tick_ms"`
	StartedAt       string              `json:"started_at"`
	FinishedAt      string              `json:"finished_at"`
	ElapsedSeconds  int64               `json:"elapsed_ms"`
	Ops             map[string]*opStats `json:"ops"`
	Note            string              `json:"note"`
}

func fatal(format string, args ...any) {
	log.Printf("loadgen: FATAL: "+format, args...)
	os.Exit(1)
}

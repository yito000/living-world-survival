// Command recoverygen は M7 再起動復旧テスト（10B 3.3 / AT-018・AT-019・AT-021）の
// バックエンド側ドライバである。scripts/recovery_test.sh から各シナリオごとに呼ばれ、
// 実 apid(:8092) / PostgreSQL / NATS に対して DS 相当の永続化操作を行い、復旧の整合を
// 検証して結果を JSON で標準出力へ 1 行返す。
//
// # DS は模擬である（重要 / スコープ）
//
// DS は Unity/FishNet であり Go/bash から駆動できない（10B 0.1/0.2 の分担では WSL2 側は
// 「バックエンド計測」。Client/DS は 10A）。したがって本ドライバは m2smoke / m6check と同じく
// **DS の RuntimePersistence 役**を演じ、WorldData / Economy gRPC を DS 相当で叩く。
// 「DS crash」は
//   - 既定: この模擬プロセスが状態を捨てて別クライアントで LoadBootstrap し直す（＝再起動）。
//   - DS=1 かつ実 DS 稼働時: recovery_test.sh が scripts/stop_ds.sh で実 DS を SIGTERM する。
//
// のいずれか。どちらで走ったかは各結果の "mode" に必ず記録し、模擬を実機と偽らない。
//
// # Client 権威を持たせない（MVP-SEC-005/006）
//
// 購入価格・課金額・付与内容はすべて EconomyService（API）が権威。ドライバは CommitPurchase を
// 投げてサーバーが確定した額をそのまま受け取るだけで、価格や在庫を決めない。
//
// # サブコマンド
//
//	scenario1        DS crash → 別 DS で snapshot(active)+event tail から復元し数一致（AT-018）
//	scenario2        購入応答直後の crash → inventory/currency 保持・二重付与/欠落なし（AT-019/021）
//	scenario5        Corrupt Snapshot を staging→checksum で弾き active は健全な直前を指す（16章）
//	s3-accumulate    NATS 停止中に events を積む（outbox 滞留）※ --state に world_id を書く
//	s3-verify        NATS 復旧後の outbox flush と inbox_dedup 一度だけ処理を検証
//	s4-setup         DB 再起動前に Buyer 登録＋購入して冪等キーを控える ※ --state へ
//	s4-verify        DB 再起動後に relay 回復・冪等再送が二重確定しないことを検証
//
// # 環境変数
//
//	API_GRPC_ADDR        既定 localhost:8092
//	DATABASE_URL_HOST    既定 postgres://survival:survival@localhost:5432/survival?sslmode=disable
//	NATS_URL_HOST        既定 nats://localhost:4222
//	WORLDSTATE_METRICS   既定 http://localhost:8083/metrics
//	RECOVERY_DS_MODE     "real" のとき mode に real-ds を記録（実 DS 併走を明示）。既定 simulated
package main

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials/insecure"

	survivalv1 "living-world-survival/services/gen/go/survival/v1"
)

// check は 1 検証項目の結果。
type check struct {
	Name   string `json:"name"`
	Status string `json:"status"` // PASS / FAIL / NODATA
	Detail string `json:"detail"`
}

// scenarioResult はシナリオ 1 本の結果。recovery_assert.py が読む生データ。
type scenarioResult struct {
	ID       string         `json:"id"`
	AT       string         `json:"at"`
	Title    string         `json:"title"`
	Mode     string         `json:"mode"` // simulated / real-ds
	Checks   []check        `json:"checks"`
	Recovery map[string]any `json:"recovery"` // purchases_lost, nonecon_restore_sec など
	Findings []string       `json:"findings"`
}

func newResult(id, at, title string) *scenarioResult {
	return &scenarioResult{
		ID: id, AT: at, Title: title, Mode: dsMode(),
		Checks: []check{}, Recovery: map[string]any{}, Findings: []string{},
	}
}

func (r *scenarioResult) add(name, status, format string, args ...any) {
	r.Checks = append(r.Checks, check{Name: name, Status: status, Detail: fmt.Sprintf(format, args...)})
}

// pass/fail はアサートの糖衣。cond が真なら PASS、偽なら FAIL を追加する。
func (r *scenarioResult) assert(name string, cond bool, format string, args ...any) {
	if cond {
		r.add(name, "PASS", format, args...)
	} else {
		r.add(name, "FAIL", format, args...)
	}
}

func (r *scenarioResult) finding(format string, args ...any) {
	r.Findings = append(r.Findings, fmt.Sprintf(format, args...))
}

// emit は結果を stdout へ 1 行 JSON で出す。recovery_test.sh がこれを収集する。
func (r *scenarioResult) emit() {
	b, err := json.Marshal(r)
	if err != nil {
		fatal("marshal result: %v", err)
	}
	fmt.Println(string(b))
}

func main() {
	if len(os.Args) < 2 {
		fatal("サブコマンドが必要です（scenario1|scenario2|scenario5|s3-accumulate|s3-verify|s4-setup|s4-verify）")
	}
	ctx := context.Background()
	switch os.Args[1] {
	case "scenario1":
		scenario1(ctx)
	case "scenario2":
		scenario2(ctx)
	case "scenario5":
		scenario5(ctx)
	case "s3-accumulate":
		s3Accumulate(ctx, flagValue("--state"))
	case "s3-verify":
		s3Verify(ctx, flagValue("--state"))
	case "s4-setup":
		s4Setup(ctx, flagValue("--state"))
	case "s4-verify":
		s4Verify(ctx, flagValue("--state"))
	default:
		fatal("未知のサブコマンド: %s", os.Args[1])
	}
}

// --- 接続ヘルパ -------------------------------------------------------------

func dbPool(ctx context.Context) *pgxpool.Pool {
	pool, err := pgxpool.New(ctx, envOr("DATABASE_URL_HOST",
		"postgres://survival:survival@localhost:5432/survival?sslmode=disable"))
	must(err, "connect postgres")
	return pool
}

func grpcConn() *grpc.ClientConn {
	conn, err := grpc.NewClient(envOr("API_GRPC_ADDR", "localhost:8092"),
		grpc.WithTransportCredentials(insecure.NewCredentials()))
	must(err, "dial gRPC")
	return conn
}

func worldDataClient(conn *grpc.ClientConn) survivalv1.WorldDataServiceClient {
	return survivalv1.NewWorldDataServiceClient(conn)
}

func economyClient(conn *grpc.ClientConn) survivalv1.EconomyServiceClient {
	return survivalv1.NewEconomyServiceClient(conn)
}

// --- 汎用ヘルパ -------------------------------------------------------------

func dsMode() string {
	if os.Getenv("RECOVERY_DS_MODE") == "real" {
		return "real-ds"
	}
	return "simulated"
}

// newUUID は crypto/rand で UUID v4 を生成する（store は internal で外部から使えない）。
func newUUID() string {
	var b [16]byte
	if _, err := rand.Read(b[:]); err != nil {
		fatal("rand: %v", err)
	}
	b[6] = (b[6] & 0x0f) | 0x40
	b[8] = (b[8] & 0x3f) | 0x80
	return fmt.Sprintf("%x-%x-%x-%x-%x", b[0:4], b[4:6], b[6:8], b[8:10], b[10:16])
}

// checksum は payload の小文字 hex SHA-256（DS 生成側と一致, 05A 落とし穴6.4）。
func checksum(payload []byte) string {
	sum := sha256.Sum256(payload)
	return hex.EncodeToString(sum[:])
}

func mkEvent(worldID, eventID string, local int64, typ, payload string) *survivalv1.DomainEvent {
	return &survivalv1.DomainEvent{
		EventId:          eventID,
		WorldId:          worldID,
		AggregateId:      newUUID(),
		LocalSequence:    local,
		Type:             typ,
		Payload:          []byte(payload),
		OccurredAtUnixMs: time.Now().UnixMilli(),
	}
}

// appendOK は 1 イベントを AppendEvents し OK を期待する。
func appendOK(ctx context.Context, wd survivalv1.WorldDataServiceClient, worldID, eventID string, local int64, typ, payload string) {
	resp, err := wd.AppendEvents(ctx, &survivalv1.AppendEventsRequest{
		ServerId: "recoverygen",
		Events:   []*survivalv1.DomainEvent{mkEvent(worldID, eventID, local, typ, payload)},
	})
	must(err, "AppendEvents "+typ)
	if resp.GetResults()[0] != survivalv1.ResultStatus_RESULT_STATUS_OK {
		fatal("AppendEvents %s: got %v want OK", typ, resp.GetResults()[0])
	}
}

func provisionWorld(ctx context.Context, pool *pgxpool.Pool) string {
	worldID := newUUID()
	_, err := pool.Exec(ctx, `INSERT INTO worlds (world_id) VALUES ($1)`, worldID)
	must(err, "provision world")
	return worldID
}

func flagValue(name string) string {
	for i := 2; i < len(os.Args)-1; i++ {
		if os.Args[i] == name {
			return os.Args[i+1]
		}
	}
	return ""
}

// state はフェーズをまたぐ受け渡し（world_id / 冪等キー等）を JSON ファイルで運ぶ。
func writeState(path string, v any) {
	if path == "" {
		fatal("--state が必要です")
	}
	b, err := json.MarshalIndent(v, "", "  ")
	must(err, "marshal state")
	must(os.WriteFile(path, b, 0o644), "write state")
}

func readState(path string, v any) {
	if path == "" {
		fatal("--state が必要です")
	}
	b, err := os.ReadFile(path)
	must(err, "read state")
	must(json.Unmarshal(b, v), "unmarshal state")
}

func envOr(key, fallback string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return fallback
}

func must(err error, what string) {
	if err != nil {
		fatal("%s: %v", what, err)
	}
}

func fatal(format string, args ...any) {
	fmt.Fprintf(os.Stderr, "recoverygen: "+format+"\n", args...)
	os.Exit(1)
}

func logf(format string, args ...any) {
	fmt.Fprintf(os.Stderr, "  · "+format+"\n", args...)
}

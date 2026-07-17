// Package integration exercises the Auth/Matchmaking stack (store + token +
// ticket + REST + gRPC) end-to-end against a real PostgreSQL. It self-skips
// when no database is reachable so `go test ./...` stays green in CI without
// infra; run `make up migrate` first (or set TEST_DATABASE_URL) to exercise it.
package integration

import (
	"bytes"
	"context"
	"crypto/ed25519"
	"encoding/base64"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"

	authgrpc "living-world-survival/services/auth/internal/grpc"
	"living-world-survival/services/auth/internal/rest"
	"living-world-survival/services/auth/internal/store"
	"living-world-survival/services/auth/internal/ticket"
	"living-world-survival/services/auth/internal/token"
	"living-world-survival/services/common/ratelimit"
	survivalv1 "living-world-survival/services/gen/go/survival/v1"
)

const (
	devSeedB64 = "bGl2aW5nLXdvcmxkLXN1cnZpdmFsLWRldi1zZWVkISE=" // matches .env dev key
	buildID    = "build-m1-test"
)

type harness struct {
	rest *httptest.Server
	grpc *authgrpc.Server

	// M7: Rate Limit 付きの REST を組み直すために依存を保持する（setupWithLimits）。
	store   *store.Store
	tokens  *token.Service
	tickets *ticket.Signer
}

func setup(t *testing.T) *harness {
	t.Helper()
	url := os.Getenv("TEST_DATABASE_URL")
	if url == "" {
		url = os.Getenv("DATABASE_URL_HOST")
	}
	if url == "" {
		url = "postgres://survival:survival@localhost:5432/survival?sslmode=disable"
	}

	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	pool, err := pgxpool.New(context.Background(), url)
	if err != nil {
		t.Skipf("no database (pool): %v", err)
	}
	if err := pool.Ping(ctx); err != nil {
		pool.Close()
		t.Skipf("no database reachable at %s: %v", url, err)
	}
	t.Cleanup(pool.Close)

	seed, _ := base64.StdEncoding.DecodeString(devSeedB64)
	priv := ed25519.NewKeyFromSeed(seed)
	pub := priv.Public().(ed25519.PublicKey)

	st := store.New(pool)
	tokens := token.NewService([]byte("integration-signing-key"), 15*time.Minute, 720*time.Hour, st)
	tickets := ticket.NewSigner(priv, pub, 60*time.Second)

	restSrv := &rest.Server{Store: st, Tokens: tokens, Tickets: tickets}
	return &harness{
		rest:    httptest.NewServer(restSrv.Handler()),
		grpc:    &authgrpc.Server{Store: st, Tickets: tickets},
		store:   st,
		tokens:  tokens,
		tickets: tickets,
	}
}

func TestFullFlow(t *testing.T) {
	h := setup(t)
	defer h.rest.Close()
	ctx := context.Background()

	email := "user-" + store.NewUUID() + "@example.com"

	// R1: create account.
	var acct struct {
		AccountID string `json:"account_id"`
	}
	code := doJSON(t, h.rest.URL, http.MethodPost, "/v1/accounts", "", map[string]string{
		"email": email, "password": "hunter2-strong", "display_name": "Tester",
	}, &acct)
	if code != http.StatusCreated {
		t.Fatalf("create account: got %d", code)
	}

	// Duplicate email → 409.
	if code := doJSON(t, h.rest.URL, http.MethodPost, "/v1/accounts", "", map[string]string{
		"email": email, "password": "hunter2-strong",
	}, nil); code != http.StatusConflict {
		t.Fatalf("duplicate email: got %d want 409", code)
	}

	// R2: login.
	var pair tokenPair
	if code := doJSON(t, h.rest.URL, http.MethodPost, "/v1/sessions", "", map[string]string{
		"email": email, "password": "hunter2-strong",
	}, &pair); code != http.StatusOK {
		t.Fatalf("login: got %d", code)
	}
	if pair.AccessToken == "" || pair.RefreshToken == "" {
		t.Fatal("login returned empty tokens")
	}

	// Wrong password → 401.
	if code := doJSON(t, h.rest.URL, http.MethodPost, "/v1/sessions", "", map[string]string{
		"email": email, "password": "wrong",
	}, nil); code != http.StatusUnauthorized {
		t.Fatalf("bad login: got %d want 401", code)
	}

	// R3: refresh rotation + reuse detection.
	var pair2 tokenPair
	if code := doJSON(t, h.rest.URL, http.MethodPost, "/v1/sessions/refresh", "", map[string]string{
		"refresh_token": pair.RefreshToken,
	}, &pair2); code != http.StatusOK {
		t.Fatalf("refresh: got %d", code)
	}
	if code := doJSON(t, h.rest.URL, http.MethodPost, "/v1/sessions/refresh", "", map[string]string{
		"refresh_token": pair.RefreshToken, // reused old token
	}, nil); code != http.StatusUnauthorized {
		t.Fatalf("refresh reuse: got %d want 401", code)
	}

	// Register + ready a Dedicated Server via gRPC.
	serverID := store.NewUUID()
	worldID := store.NewUUID()
	if resp, err := h.grpc.RegisterServer(ctx, &survivalv1.RegisterServerRequest{
		ServerId: serverID, WorldId: worldID, BuildId: buildID,
		Endpoint: "127.0.0.1:7777", Capacity: 10,
	}); err != nil || !resp.GetOk() {
		t.Fatalf("register server: resp=%v err=%v", resp, err)
	}
	if resp, err := h.grpc.Heartbeat(ctx, &survivalv1.HeartbeatRequest{
		ServerId: serverID, Ready: true, Players: 0, TickMs: 33,
	}); err != nil || !resp.GetOk() {
		t.Fatalf("heartbeat: resp=%v err=%v", resp, err)
	}

	// R5: matchmaking join (need a fresh access token from pair2).
	var join struct {
		ServerEndpoint string `json:"server_endpoint"`
		JoinTicket     string `json:"join_ticket"`
		ExpiresAt      string `json:"expires_at"`
	}
	characterID := store.NewUUID()
	if code := doJSON(t, h.rest.URL, http.MethodPost, "/v1/matchmaking/join", pair2.AccessToken, map[string]string{
		"character_id": characterID, "build_id": buildID,
	}, &join); code != http.StatusOK {
		t.Fatalf("join: got %d", code)
	}
	if join.JoinTicket == "" || join.ServerEndpoint != "127.0.0.1:7777" {
		t.Fatalf("join response bad: %+v", join)
	}

	// G1: redeem once ok, twice already_used (single-use).
	r1, err := h.grpc.RedeemJoinTicket(ctx, &survivalv1.RedeemJoinTicketRequest{ServerId: serverID, Ticket: join.JoinTicket})
	if err != nil || !r1.GetOk() {
		t.Fatalf("redeem #1: resp=%v err=%v", r1, err)
	}
	if r1.GetClaims().GetCharacterId() != characterID {
		t.Fatalf("redeem claims character mismatch: %v", r1.GetClaims())
	}
	r2, err := h.grpc.RedeemJoinTicket(ctx, &survivalv1.RedeemJoinTicketRequest{ServerId: serverID, Ticket: join.JoinTicket})
	if err != nil {
		t.Fatalf("redeem #2 err: %v", err)
	}
	if r2.GetOk() || r2.GetError() != "already_used" {
		t.Fatalf("redeem #2: got ok=%v error=%q want already_used", r2.GetOk(), r2.GetError())
	}

	// Server mismatch: a fresh ticket redeemed at the wrong server.
	var join2 struct {
		JoinTicket string `json:"join_ticket"`
	}
	doJSON(t, h.rest.URL, http.MethodPost, "/v1/matchmaking/join", pair2.AccessToken, map[string]string{
		"character_id": store.NewUUID(), "build_id": buildID,
	}, &join2)
	rm, err := h.grpc.RedeemJoinTicket(ctx, &survivalv1.RedeemJoinTicketRequest{ServerId: store.NewUUID(), Ticket: join2.JoinTicket})
	if err != nil {
		t.Fatalf("redeem mismatch err: %v", err)
	}
	if rm.GetOk() || rm.GetError() != "server_mismatch" {
		t.Fatalf("redeem mismatch: got ok=%v error=%q want server_mismatch", rm.GetOk(), rm.GetError())
	}

	// R4: logout succeeds with a valid access token.
	if code := doJSON(t, h.rest.URL, http.MethodDelete, "/v1/sessions/current", pair2.AccessToken, nil, nil); code != http.StatusNoContent {
		t.Fatalf("logout: got %d want 204", code)
	}
}

func TestJoinNoReadyServer(t *testing.T) {
	h := setup(t)
	defer h.rest.Close()

	email := "user-" + store.NewUUID() + "@example.com"
	var acct struct {
		AccountID string `json:"account_id"`
	}
	doJSON(t, h.rest.URL, http.MethodPost, "/v1/accounts", "", map[string]string{
		"email": email, "password": "hunter2-strong",
	}, &acct)
	var pair tokenPair
	doJSON(t, h.rest.URL, http.MethodPost, "/v1/sessions", "", map[string]string{
		"email": email, "password": "hunter2-strong",
	}, &pair)

	// No server registered for this unique build → 503.
	if code := doJSON(t, h.rest.URL, http.MethodPost, "/v1/matchmaking/join", pair.AccessToken, map[string]string{
		"character_id": store.NewUUID(), "build_id": "no-such-build-" + store.NewUUID(),
	}, nil); code != http.StatusServiceUnavailable {
		t.Fatalf("join no server: got %d want 503", code)
	}
}

type tokenPair struct {
	AccessToken  string `json:"access_token"`
	RefreshToken string `json:"refresh_token"`
	ExpiresIn    int64  `json:"expires_in"`
}

// doJSON performs a JSON request and optionally decodes the response, returning
// the HTTP status code.
func doJSON(t *testing.T, base, method, path, bearer string, body any, out any) int {
	t.Helper()
	var buf bytes.Buffer
	if body != nil {
		if err := json.NewEncoder(&buf).Encode(body); err != nil {
			t.Fatalf("encode body: %v", err)
		}
	}
	req, err := http.NewRequest(method, base+path, &buf)
	if err != nil {
		t.Fatalf("new request: %v", err)
	}
	if body != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	if bearer != "" {
		req.Header.Set("Authorization", "Bearer "+bearer)
	}
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatalf("do request %s %s: %v", method, path, err)
	}
	defer func() { _ = resp.Body.Close() }()
	if out != nil && resp.StatusCode < 300 {
		if err := json.NewDecoder(resp.Body).Decode(out); err != nil && !strings.Contains(err.Error(), "EOF") {
			t.Fatalf("decode response: %v", err)
		}
	}
	return resp.StatusCode
}

// --- M7 / 第16章 Auth 失敗の Rate Limit（10B 3.4・MVP-SEC-005）-----------------

// setupWithLimits は Rate Limit を効かせた REST を立てる。既定の setup は
// Limiter を nil にしており（＝制限なし）、他のテストは影響を受けない。
func setupWithLimits(t *testing.T, loginRate int) *harness {
	t.Helper()
	h := setup(t)
	h.rest.Close()

	restSrv := &rest.Server{
		Store: h.store, Tokens: h.tokens, Tickets: h.tickets,
		LoginLimiter:   ratelimit.New(loginRate, time.Minute, loginRate),
		AccountLimiter: ratelimit.New(100, time.Minute, 100),
	}
	h.rest = httptest.NewServer(restSrv.Handler())
	return h
}

func createAccount(t *testing.T, h *harness, email, pw string) {
	t.Helper()
	code := doJSON(t, h.rest.URL, http.MethodPost, "/v1/accounts", "", map[string]string{
		"email": email, "password": pw, "display_name": "RateLimit Tester",
	}, nil)
	if code != http.StatusCreated {
		t.Fatalf("create account: got %d", code)
	}
}

func login(t *testing.T, h *harness, email, pw string) int {
	t.Helper()
	return doJSON(t, h.rest.URL, http.MethodPost, "/v1/sessions", "",
		map[string]string{"email": email, "password": pw}, nil)
}

// 失敗が閾値に達したら 429 で遮断する（総当りの抑止）。
func TestLoginRateLimitBlocksAfterRepeatedFailures(t *testing.T) {
	h := setupWithLimits(t, 3)
	defer h.rest.Close()

	email := "ratelimit-" + store.NewUUID() + "@example.com"
	createAccount(t, h, email, "hunter2-strong")

	for i := 1; i <= 3; i++ {
		if code := login(t, h, email, "wrong-password"); code != http.StatusUnauthorized {
			t.Fatalf("failure %d: got %d want 401", i, code)
		}
	}
	// 4 回目は認証まで到達せず 429。
	if code := login(t, h, email, "wrong-password"); code != http.StatusTooManyRequests {
		t.Fatalf("4th failed login: got %d want 429", code)
	}
	// 遮断中は正しいパスワードでも通さない（枠を使い切っているため）。
	if code := login(t, h, email, "hunter2-strong"); code != http.StatusTooManyRequests {
		t.Fatalf("login while rate limited: got %d want 429", code)
	}
}

// 成功はトークンを消費しない。正規利用者が何度ログインしても遮断されないこと。
func TestLoginRateLimitDoesNotPenalizeSuccess(t *testing.T) {
	h := setupWithLimits(t, 3)
	defer h.rest.Close()

	email := "ratelimit-ok-" + store.NewUUID() + "@example.com"
	createAccount(t, h, email, "hunter2-strong")

	for i := 1; i <= 10; i++ {
		if code := login(t, h, email, "hunter2-strong"); code != http.StatusOK {
			t.Fatalf("successful login %d: got %d want 200 (success must not consume tokens)", i, code)
		}
	}
}

// 存在しない email での失敗も数える（列挙＋総当りの両方を抑止）。
func TestLoginRateLimitCountsUnknownEmailFailures(t *testing.T) {
	h := setupWithLimits(t, 2)
	defer h.rest.Close()

	for i := 1; i <= 2; i++ {
		if code := login(t, h, "nobody-"+store.NewUUID()+"@example.com", "x"); code != http.StatusUnauthorized {
			t.Fatalf("unknown-email failure %d: got %d want 401", i, code)
		}
	}
	if code := login(t, h, "nobody-"+store.NewUUID()+"@example.com", "x"); code != http.StatusTooManyRequests {
		t.Fatalf("3rd unknown-email failure: got %d want 429", code)
	}
}

// 閾値 0（無効化）では制限しない。誤設定で全拒否にならないこと。
func TestLoginRateLimitDisabled(t *testing.T) {
	h := setupWithLimits(t, 0)
	defer h.rest.Close()

	email := "ratelimit-off-" + store.NewUUID() + "@example.com"
	createAccount(t, h, email, "hunter2-strong")

	for i := 1; i <= 20; i++ {
		if code := login(t, h, email, "wrong"); code != http.StatusUnauthorized {
			t.Fatalf("failure %d with limiting disabled: got %d want 401", i, code)
		}
	}
}

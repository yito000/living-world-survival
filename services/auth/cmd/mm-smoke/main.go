// Command mm-smoke drives the Auth/Matchmaking E2E path used by scripts/smoke.sh
// (5.3): create account → login → RegisterServer/Heartbeat → matchmaking join →
// RedeemJoinTicket (single-use: 1st ok, 2nd error). It talks REST (AUTH_PORT)
// and internal gRPC (AUTH_GRPC_PORT), and exits non-zero on the first failure.
package main

import (
	"bytes"
	"context"
	"crypto/rand"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"net/http"
	"os"
	"time"

	grpclib "google.golang.org/grpc"
	"google.golang.org/grpc/credentials/insecure"

	survivalv1 "living-world-survival/services/gen/go/survival/v1"
)

func main() {
	host := envOr("SMOKE_HOST", "localhost")
	restBase := fmt.Sprintf("http://%s:%s", host, envOr("AUTH_PORT", "8081"))
	grpcAddr := fmt.Sprintf("%s:%s", host, envOr("AUTH_GRPC_PORT", "9091"))
	buildID := envOr("SMOKE_BUILD_ID", "smoke-build-1")

	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()

	email := "smoke-" + uuid() + "@example.com"

	// 1. Create account.
	must(postJSON(restBase, "/v1/accounts", "", map[string]string{
		"email": email, "password": "smoke-pass-123", "display_name": "smoke",
	}, http.StatusCreated, nil), "create account")
	log.Printf("mm-smoke: account created (%s)", email)

	// 2. Login.
	var login struct {
		AccessToken  string `json:"access_token"`
		RefreshToken string `json:"refresh_token"`
	}
	must(postJSON(restBase, "/v1/sessions", "", map[string]string{
		"email": email, "password": "smoke-pass-123",
	}, http.StatusOK, &login), "login")
	if login.AccessToken == "" {
		fail("login returned empty access token")
	}
	log.Printf("mm-smoke: logged in")

	// gRPC dial.
	conn, err := grpclib.NewClient(grpcAddr, grpclib.WithTransportCredentials(insecure.NewCredentials()))
	if err != nil {
		fail("grpc dial %s: %v", grpcAddr, err)
	}
	defer func() { _ = conn.Close() }()
	mm := survivalv1.NewMatchmakingServiceClient(conn)

	// 3. Register + ready a dummy Dedicated Server.
	serverID, worldID := uuid(), uuid()
	reg, err := mm.RegisterServer(ctx, &survivalv1.RegisterServerRequest{
		ServerId: serverID, WorldId: worldID, BuildId: buildID,
		Endpoint: "127.0.0.1:7777", Capacity: 8,
	})
	if err != nil || !reg.GetOk() {
		fail("RegisterServer: resp=%v err=%v", reg, err)
	}
	hb, err := mm.Heartbeat(ctx, &survivalv1.HeartbeatRequest{ServerId: serverID, Ready: true, TickMs: 33})
	if err != nil || !hb.GetOk() {
		fail("Heartbeat: resp=%v err=%v", hb, err)
	}
	log.Printf("mm-smoke: server registered + ready (%s)", serverID)

	// 4. Matchmaking join.
	var join struct {
		ServerEndpoint string `json:"server_endpoint"`
		JoinTicket     string `json:"join_ticket"`
		ExpiresAt      string `json:"expires_at"`
	}
	must(postJSON(restBase, "/v1/matchmaking/join", login.AccessToken, map[string]string{
		"character_id": uuid(), "build_id": buildID,
	}, http.StatusOK, &join), "matchmaking join")
	if join.JoinTicket == "" {
		fail("join returned empty ticket")
	}
	log.Printf("mm-smoke: joined, endpoint=%s expires_at=%s", join.ServerEndpoint, join.ExpiresAt)

	// 5. Redeem #1 (ok) / #2 (already used) — single-use.
	r1, err := mm.RedeemJoinTicket(ctx, &survivalv1.RedeemJoinTicketRequest{ServerId: serverID, Ticket: join.JoinTicket})
	if err != nil || !r1.GetOk() {
		fail("RedeemJoinTicket #1: resp=%v err=%v", r1, err)
	}
	r2, err := mm.RedeemJoinTicket(ctx, &survivalv1.RedeemJoinTicketRequest{ServerId: serverID, Ticket: join.JoinTicket})
	if err != nil {
		fail("RedeemJoinTicket #2 err: %v", err)
	}
	if r2.GetOk() {
		fail("RedeemJoinTicket #2 unexpectedly succeeded (single-use violated)")
	}
	log.Printf("mm-smoke: redeem #1 ok, #2 rejected (%s) — single-use OK", r2.GetError())

	fmt.Println("mm-smoke: E2E OK")
}

func postJSON(base, path, bearer string, body any, wantStatus int, out any) error {
	var buf bytes.Buffer
	if err := json.NewEncoder(&buf).Encode(body); err != nil {
		return err
	}
	req, err := http.NewRequest(http.MethodPost, base+path, &buf)
	if err != nil {
		return err
	}
	req.Header.Set("Content-Type", "application/json")
	if bearer != "" {
		req.Header.Set("Authorization", "Bearer "+bearer)
	}
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		return err
	}
	defer func() { _ = resp.Body.Close() }()
	raw, _ := io.ReadAll(resp.Body)
	if resp.StatusCode != wantStatus {
		return fmt.Errorf("status %d (want %d): %s", resp.StatusCode, wantStatus, string(raw))
	}
	if out != nil {
		if err := json.Unmarshal(raw, out); err != nil {
			return fmt.Errorf("decode: %w", err)
		}
	}
	return nil
}

func must(err error, what string) {
	if err != nil {
		fail("%s: %v", what, err)
	}
}

func fail(format string, args ...any) {
	log.Printf("mm-smoke: FAIL: "+format, args...)
	os.Exit(1)
}

func envOr(k, def string) string {
	if v := os.Getenv(k); v != "" {
		return v
	}
	return def
}

func uuid() string {
	var b [16]byte
	if _, err := rand.Read(b[:]); err != nil {
		panic(err)
	}
	b[6] = (b[6] & 0x0f) | 0x40
	b[8] = (b[8] & 0x3f) | 0x80
	return fmt.Sprintf("%x-%x-%x-%x-%x", b[0:4], b[4:6], b[6:8], b[8:10], b[10:16])
}

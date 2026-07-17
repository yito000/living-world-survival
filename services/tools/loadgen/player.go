package main

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"time"
)

// playerSession は 1 プレイヤーの接続前フロー（M1）を通す:
// アカウント作成 → ログイン → Matchmaking join → 合成 DS 側で RedeemJoinTicket。
// mm-smoke（services/auth/cmd/mm-smoke）と同じ経路を、N 本並列で回すだけである。
//
// ここから先（Client→DS の FishNet 接続と同期）は 10A の PlayMode 負荷の担当であり、
// 本ドライバは踏み込まない。Join まででバックエンド側の接続コストは出し切れる。
type playerSession struct {
	cfg   config
	ds    *syntheticDS
	stats *stats
	name  string

	email        string
	accessToken  string
	refreshToken string
}

const playerPassword = "loadgen-pass-123"

func (p *playerSession) join(ctx context.Context) error {
	p.email = "loadgen-" + newUUID() + "@example.com"

	// 1) アカウント作成
	if err := p.timed(ctx, "auth_create_account", func(ctx context.Context) error {
		return postJSON(ctx, p.cfg.RESTBase, "/v1/accounts", "", map[string]string{
			"email": p.email, "password": playerPassword, "display_name": p.name,
		}, http.StatusCreated, nil)
	}); err != nil {
		return fmt.Errorf("アカウント作成: %w", err)
	}

	// 2) ログイン
	var login struct {
		AccessToken  string `json:"access_token"`
		RefreshToken string `json:"refresh_token"`
	}
	if err := p.timed(ctx, "auth_login", func(ctx context.Context) error {
		return postJSON(ctx, p.cfg.RESTBase, "/v1/sessions", "", map[string]string{
			"email": p.email, "password": playerPassword,
		}, http.StatusOK, &login)
	}); err != nil {
		return fmt.Errorf("ログイン: %w", err)
	}
	p.accessToken, p.refreshToken = login.AccessToken, login.RefreshToken

	// 3) Matchmaking join。build_id は合成 DS と同じ値にする（auth は build_id で候補を絞る）。
	//    実 DS 併走時に実 DS の build_id と別値にしておくことで、取り違えて実 DS の
	//    ticket を合成 DS で redeem する事故を防ぐ。
	var joined struct {
		ServerEndpoint string `json:"server_endpoint"`
		JoinTicket     string `json:"join_ticket"`
	}
	if err := p.timed(ctx, "matchmaking_join", func(ctx context.Context) error {
		return postJSON(ctx, p.cfg.RESTBase, "/v1/matchmaking/join", p.accessToken, map[string]string{
			"character_id": newUUID(), "build_id": p.cfg.BuildID,
		}, http.StatusOK, &joined)
	}); err != nil {
		return fmt.Errorf("matchmaking join: %w", err)
	}
	if joined.JoinTicket == "" {
		return fmt.Errorf("join ticket が空")
	}

	// 4) DS 側での単回消費（G2）。ticket の検証・消費はサーバー権威。
	if err := p.timed(ctx, "redeem_join_ticket", func(ctx context.Context) error {
		return p.ds.redeem(ctx, joined.JoinTicket)
	}); err != nil {
		return fmt.Errorf("RedeemJoinTicket: %w", err)
	}
	return nil
}

// timed は 1 操作を計測して stats に積む。RTT は参考値（Gate ではない）。
func (p *playerSession) timed(ctx context.Context, kind string, fn func(context.Context) error) error {
	opCtx, cancel := context.WithTimeout(ctx, 15*time.Second)
	defer cancel()
	start := time.Now()
	err := fn(opCtx)
	elapsed := time.Since(start)
	if err != nil {
		p.stats.record(kind, elapsed, "error", err)
		return err
	}
	p.stats.record(kind, elapsed, "ok", nil)
	return nil
}

func postJSON(ctx context.Context, base, path, bearer string, body any, wantStatus int, out any) error {
	var buf bytes.Buffer
	if err := json.NewEncoder(&buf).Encode(body); err != nil {
		return err
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, base+path, &buf)
	if err != nil {
		return err
	}
	req.Header.Set("Content-Type", "application/json")
	if bearer != "" {
		req.Header.Set("Authorization", "Bearer "+bearer)
	}
	resp, err := httpClient.Do(req)
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

// httpClient は N プレイヤー分の接続を使い回す。既定の Transport は
// MaxIdleConnsPerHost=2 で、同時プレイヤー数を増やすと毎回 TCP を張り直して
// 「バックエンドではなくドライバ側」がボトルネックになる。
var httpClient = &http.Client{
	Timeout: 15 * time.Second,
	Transport: &http.Transport{
		MaxIdleConns:        256,
		MaxIdleConnsPerHost: 256,
		IdleConnTimeout:     60 * time.Second,
	},
}

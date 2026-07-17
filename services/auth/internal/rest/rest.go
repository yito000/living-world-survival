// Package rest implements the Client-facing Auth/Matchmaking REST API (3.1-3.2,
// MVP 11.1). All errors use the {"error":{"code","message"}} envelope.
package rest

import (
	"context"
	"encoding/json"
	"errors"
	"net"
	"net/http"
	"net/mail"
	"strings"
	"time"

	"living-world-survival/services/auth/internal/metrics"
	"living-world-survival/services/auth/internal/password"
	"living-world-survival/services/auth/internal/store"
	"living-world-survival/services/auth/internal/ticket"
	"living-world-survival/services/auth/internal/token"
	"living-world-survival/services/common/obs"
	"living-world-survival/services/common/ratelimit"
)

// Server holds the dependencies of the REST handlers.
type Server struct {
	Store   *store.Store
	Tokens  *token.Service
	Tickets *ticket.Signer

	// LoginLimiter はログイン失敗の Rate Limit（第16章 / MVP-SEC-005）。
	// nil なら制限なし（ratelimit.Limiter は nil レシーバで常に許可する）。
	LoginLimiter *ratelimit.Limiter
	// AccountLimiter はアカウント作成の Rate Limit。
	AccountLimiter *ratelimit.Limiter
}

// Handler returns the routed http.Handler for the /v1 REST surface. It is
// mounted alongside the existing /healthz and /readyz endpoints.
func (s *Server) Handler() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("POST /v1/accounts", s.handleCreateAccount)
	mux.HandleFunc("POST /v1/sessions", s.handleLogin)
	mux.HandleFunc("POST /v1/sessions/refresh", s.handleRefresh)
	mux.HandleFunc("DELETE /v1/sessions/current", s.handleLogout)
	mux.HandleFunc("POST /v1/matchmaking/join", s.handleJoin)
	return mux
}

// --- R1: POST /v1/accounts --------------------------------------------------

type createAccountReq struct {
	Email       string `json:"email"`
	Password    string `json:"password"`
	DisplayName string `json:"display_name"`
}

func (s *Server) handleCreateAccount(w http.ResponseWriter, r *http.Request) {
	var req createAccountReq
	if !decode(w, r, &req) {
		return
	}
	// 総当り登録の抑止。作成は成功しても資源を食うので、試行そのものを数える。
	if !s.AccountLimiter.Allow(clientIP(r)) {
		metrics.RateLimited.WithLabelValues("account_create").Inc()
		obs.L(r.Context()).Warn("account create rate limited", "audit", true)
		writeError(w, http.StatusTooManyRequests, "rate_limited",
			"too many account creations; try again later")
		return
	}
	if _, err := mail.ParseAddress(req.Email); err != nil {
		writeError(w, http.StatusBadRequest, "invalid_email", "email is not a valid address")
		return
	}
	if len(req.Password) < 8 {
		writeError(w, http.StatusBadRequest, "weak_password", "password must be at least 8 characters")
		return
	}
	hash, err := password.Hash(req.Password)
	if err != nil {
		serverError(r.Context(), w, "hash password", err)
		return
	}
	accountID, err := s.Store.CreateAccount(r.Context(), req.Email, hash, req.DisplayName)
	if errors.Is(err, store.ErrEmailExists) {
		writeError(w, http.StatusConflict, "email_taken", "email is already registered")
		return
	}
	if err != nil {
		serverError(r.Context(), w, "create account", err)
		return
	}
	// email は個人情報なのでログに出さない。account_id で相関する。
	obs.L(obs.WithFields(r.Context(), obs.Fields{AccountID: accountID})).
		Info("account created")
	writeJSON(w, http.StatusCreated, map[string]string{"account_id": accountID})
}

// --- R2: POST /v1/sessions --------------------------------------------------

type loginReq struct {
	Email    string `json:"email"`
	Password string `json:"password"`
}

type tokenPairResp struct {
	AccessToken  string `json:"access_token"`
	RefreshToken string `json:"refresh_token"`
	ExpiresIn    int64  `json:"expires_in"`
}

func (s *Server) handleLogin(w http.ResponseWriter, r *http.Request) {
	var req loginReq
	if !decode(w, r, &req) {
		return
	}

	// 第16章 Auth 失敗の Rate Limit。失敗だけを数え、成功は消費しない
	// （正規利用者は何回ログインしても遮断されない）。
	limitKey := clientIP(r)
	if !s.LoginLimiter.Peek(limitKey) {
		metrics.LoginAttempts.WithLabelValues("rate_limited").Inc()
		metrics.RateLimited.WithLabelValues("login").Inc()
		obs.L(r.Context()).Warn("login rate limited", "audit", true)
		writeError(w, http.StatusTooManyRequests, "rate_limited",
			"too many failed login attempts; try again later")
		return
	}

	cred, err := s.Store.GetCredentialByEmail(r.Context(), req.Email)
	if errors.Is(err, store.ErrNotFound) {
		// 存在しない email と誤ったパスワードは同じ応答にする（列挙防止）。
		s.LoginLimiter.Consume(limitKey)
		loginFailed(r.Context())
		writeError(w, http.StatusUnauthorized, "invalid_credentials", "email or password is incorrect")
		return
	}
	if err != nil {
		// DB 障害は利用者の失敗ではないのでトークンを消費しない。
		serverError(r.Context(), w, "lookup credential", err)
		return
	}
	if err := password.Verify(req.Password, cred.PasswordHash); err != nil {
		s.LoginLimiter.Consume(limitKey)
		loginFailed(obs.WithFields(r.Context(), obs.Fields{AccountID: cred.AccountID}))
		writeError(w, http.StatusUnauthorized, "invalid_credentials", "email or password is incorrect")
		return
	}
	pair, err := s.Tokens.NewSession(r.Context(), cred.AccountID)
	if err != nil {
		serverError(r.Context(), w, "issue session", err)
		return
	}
	metrics.LoginAttempts.WithLabelValues("success").Inc()
	obs.L(obs.WithFields(r.Context(), obs.Fields{AccountID: cred.AccountID})).
		Info("login succeeded", "audit", true)
	writeJSON(w, http.StatusOK, tokenPairResp{pair.AccessToken, pair.RefreshToken, pair.ExpiresIn})
}

// --- R3: POST /v1/sessions/refresh ------------------------------------------

type refreshReq struct {
	RefreshToken string `json:"refresh_token"`
}

func (s *Server) handleRefresh(w http.ResponseWriter, r *http.Request) {
	var req refreshReq
	if !decode(w, r, &req) {
		return
	}
	if req.RefreshToken == "" {
		writeError(w, http.StatusBadRequest, "missing_token", "refresh_token is required")
		return
	}
	pair, err := s.Tokens.Refresh(r.Context(), req.RefreshToken)
	if errors.Is(err, token.ErrReuseDetected) {
		// 再利用検知＝family 失効（RFC 9700 / MVP-SEC-003）。盗用の可能性が
		// あるので、単なる無効トークンとは別系列で監査に残す。
		metrics.RefreshRotations.WithLabelValues("reuse_detected").Inc()
		obs.L(r.Context()).Warn("refresh token reuse detected; family revoked", "audit", true)
		writeError(w, http.StatusUnauthorized, "invalid_refresh", "refresh token is invalid, expired, or reused")
		return
	}
	if errors.Is(err, token.ErrInvalidRefresh) {
		metrics.RefreshRotations.WithLabelValues("invalid").Inc()
		writeError(w, http.StatusUnauthorized, "invalid_refresh", "refresh token is invalid, expired, or reused")
		return
	}
	if err != nil {
		serverError(r.Context(), w, "refresh session", err)
		return
	}
	metrics.RefreshRotations.WithLabelValues("rotated").Inc()
	writeJSON(w, http.StatusOK, tokenPairResp{pair.AccessToken, pair.RefreshToken, pair.ExpiresIn})
}

// --- R4: DELETE /v1/sessions/current ----------------------------------------

func (s *Server) handleLogout(w http.ResponseWriter, r *http.Request) {
	claims, ok := s.authenticate(w, r)
	if !ok {
		return
	}
	if err := s.Tokens.Logout(r.Context(), claims.FamilyID); err != nil {
		serverError(r.Context(), w, "logout", err)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

// --- R5: POST /v1/matchmaking/join ------------------------------------------

type joinReq struct {
	CharacterID string `json:"character_id"`
	BuildID     string `json:"build_id"`
}

type joinResp struct {
	ServerEndpoint string `json:"server_endpoint"`
	JoinTicket     string `json:"join_ticket"`
	ExpiresAt      string `json:"expires_at"`
}

func (s *Server) handleJoin(w http.ResponseWriter, r *http.Request) {
	claims, ok := s.authenticate(w, r)
	if !ok {
		return
	}
	var req joinReq
	if !decode(w, r, &req) {
		return
	}
	if req.BuildID == "" {
		writeError(w, http.StatusBadRequest, "missing_build_id", "build_id is required")
		return
	}
	// M1 simplifies Character existence to a format check; the DS Authenticator
	// (04A) makes the final rejection of invalid characters.
	if !isUUID(req.CharacterID) {
		writeError(w, http.StatusBadRequest, "invalid_character", "character_id must be a UUID")
		return
	}

	ctx := obs.WithFields(r.Context(), obs.Fields{AccountID: claims.Subject})

	srv, err := s.Store.SelectReadyServer(ctx, req.BuildID)
	if errors.Is(err, store.ErrNoServer) {
		writeError(w, http.StatusServiceUnavailable, "no_server", "no ready server for this build")
		return
	}
	if err != nil {
		serverError(ctx, w, "select server", err)
		return
	}
	ctx = obs.WithFields(ctx, obs.Fields{ServerID: srv.ServerID, WorldID: srv.WorldID})

	tc, tok, err := s.Tickets.Issue(claims.Subject, req.CharacterID, srv.ServerID, srv.WorldID, srv.BuildID)
	if err != nil {
		serverError(ctx, w, "issue ticket", err)
		return
	}
	if err := s.Store.InsertJoinTicket(ctx, store.JoinTicket{
		TicketID:    tc.GetTicketId(),
		AccountID:   tc.GetAccountId(),
		CharacterID: tc.GetCharacterId(),
		ServerID:    tc.GetServerId(),
		WorldID:     tc.GetWorldId(),
		BuildID:     tc.GetBuildId(),
		Nonce:       tc.GetNonce(),
		IssuedAt:    time.UnixMilli(tc.GetIssuedAtUnixMs()),
		ExpiresAt:   time.UnixMilli(tc.GetExpiresAtUnixMs()),
	}); err != nil {
		serverError(ctx, w, "persist ticket", err)
		return
	}
	// Ticket 発行は監査対象（MVP-SEC-009）。tok（署名済みチケット本体）は出さない。
	obs.L(ctx).Info("join ticket issued",
		"audit", true, "ticket_id", tc.GetTicketId(), "build_id", srv.BuildID)
	writeJSON(w, http.StatusOK, joinResp{
		ServerEndpoint: srv.Endpoint,
		JoinTicket:     tok,
		ExpiresAt:      time.UnixMilli(tc.GetExpiresAtUnixMs()).UTC().Format(time.RFC3339),
	})
}

// --- helpers ----------------------------------------------------------------

// loginFailed はログイン失敗を metrics と監査ログへ残す（第16章 Auth 失敗の
// Rate Limit / MVP-SEC-009）。email も入力パスワードも出さない（MVP-SEC-002）。
func loginFailed(ctx context.Context) {
	metrics.LoginAttempts.WithLabelValues("invalid_credentials").Inc()
	obs.L(ctx).Warn("login failed", "audit", true, "reason", "invalid_credentials")
}

// clientIP は Rate Limit のキーに使う送信元アドレスを返す。
//
// X-Forwarded-For は **意図的に見ない**。信頼できる Reverse Proxy が前段に
// いる保証が無い状態でこのヘッダを信じると、攻撃者が値を詐称するだけで
// キーを無限に変えられ、Rate Limit が素通りする。前段に Proxy を置く構成に
// する際は、Proxy の実アドレスを許可リスト化した上でここを変更すること。
func clientIP(r *http.Request) string {
	host, _, err := net.SplitHostPort(r.RemoteAddr)
	if err != nil {
		// ポートが付いていない形式なら、そのまま使う。
		return r.RemoteAddr
	}
	return host
}

// authenticate validates the Bearer access token and returns its claims.
func (s *Server) authenticate(w http.ResponseWriter, r *http.Request) (*token.AccessClaims, bool) {
	h := r.Header.Get("Authorization")
	if !strings.HasPrefix(h, "Bearer ") {
		writeError(w, http.StatusUnauthorized, "missing_token", "Authorization: Bearer <access_token> required")
		return nil, false
	}
	claims, err := s.Tokens.ParseAccess(strings.TrimSpace(strings.TrimPrefix(h, "Bearer ")))
	if err != nil {
		writeError(w, http.StatusUnauthorized, "invalid_token", "access token is invalid or expired")
		return nil, false
	}
	return claims, true
}

func decode(w http.ResponseWriter, r *http.Request, dst any) bool {
	dec := json.NewDecoder(http.MaxBytesReader(w, r.Body, 1<<20))
	dec.DisallowUnknownFields()
	if err := dec.Decode(dst); err != nil {
		writeError(w, http.StatusBadRequest, "invalid_body", "request body is not valid JSON")
		return false
	}
	return true
}

func writeJSON(w http.ResponseWriter, status int, body any) {
	obs.WriteJSON(w, status, body)
}

type errorEnvelope struct {
	Error errorBody `json:"error"`
}

type errorBody struct {
	Code    string `json:"code"`
	Message string `json:"message"`
}

func writeError(w http.ResponseWriter, status int, code, message string) {
	writeJSON(w, status, errorEnvelope{errorBody{Code: code, Message: message}})
}

// serverError は内部エラーをログへ残し、Client には詳細を返さない。
// err の中身（DSN や SQL 断片）を応答に載せないのが要点（MVP-SEC-002）。
func serverError(ctx context.Context, w http.ResponseWriter, what string, err error) {
	obs.L(ctx).Error("request failed", "op", what, "error", err.Error())
	writeError(w, http.StatusInternalServerError, "internal", "internal server error")
}

// isUUID reports whether s is a canonical 8-4-4-4-12 hex UUID.
func isUUID(s string) bool {
	if len(s) != 36 {
		return false
	}
	for i, c := range s {
		switch i {
		case 8, 13, 18, 23:
			if c != '-' {
				return false
			}
		default:
			isHex := (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')
			if !isHex {
				return false
			}
		}
	}
	return true
}

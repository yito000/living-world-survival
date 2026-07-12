// Package rest implements the Client-facing Auth/Matchmaking REST API (3.1-3.2,
// MVP 11.1). All errors use the {"error":{"code","message"}} envelope.
package rest

import (
	"encoding/json"
	"errors"
	"log"
	"net/http"
	"net/mail"
	"strings"
	"time"

	"living-world-survival/services/auth/internal/password"
	"living-world-survival/services/auth/internal/store"
	"living-world-survival/services/auth/internal/ticket"
	"living-world-survival/services/auth/internal/token"
)

// Server holds the dependencies of the REST handlers.
type Server struct {
	Store   *store.Store
	Tokens  *token.Service
	Tickets *ticket.Signer
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
		serverError(w, "hash password", err)
		return
	}
	accountID, err := s.Store.CreateAccount(r.Context(), req.Email, hash, req.DisplayName)
	if errors.Is(err, store.ErrEmailExists) {
		writeError(w, http.StatusConflict, "email_taken", "email is already registered")
		return
	}
	if err != nil {
		serverError(w, "create account", err)
		return
	}
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
	cred, err := s.Store.GetCredentialByEmail(r.Context(), req.Email)
	if errors.Is(err, store.ErrNotFound) {
		writeError(w, http.StatusUnauthorized, "invalid_credentials", "email or password is incorrect")
		return
	}
	if err != nil {
		serverError(w, "lookup credential", err)
		return
	}
	if err := password.Verify(req.Password, cred.PasswordHash); err != nil {
		writeError(w, http.StatusUnauthorized, "invalid_credentials", "email or password is incorrect")
		return
	}
	pair, err := s.Tokens.NewSession(r.Context(), cred.AccountID)
	if err != nil {
		serverError(w, "issue session", err)
		return
	}
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
	if errors.Is(err, token.ErrInvalidRefresh) || errors.Is(err, token.ErrReuseDetected) {
		writeError(w, http.StatusUnauthorized, "invalid_refresh", "refresh token is invalid, expired, or reused")
		return
	}
	if err != nil {
		serverError(w, "refresh session", err)
		return
	}
	writeJSON(w, http.StatusOK, tokenPairResp{pair.AccessToken, pair.RefreshToken, pair.ExpiresIn})
}

// --- R4: DELETE /v1/sessions/current ----------------------------------------

func (s *Server) handleLogout(w http.ResponseWriter, r *http.Request) {
	claims, ok := s.authenticate(w, r)
	if !ok {
		return
	}
	if err := s.Tokens.Logout(r.Context(), claims.FamilyID); err != nil {
		serverError(w, "logout", err)
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

	srv, err := s.Store.SelectReadyServer(r.Context(), req.BuildID)
	if errors.Is(err, store.ErrNoServer) {
		writeError(w, http.StatusServiceUnavailable, "no_server", "no ready server for this build")
		return
	}
	if err != nil {
		serverError(w, "select server", err)
		return
	}

	tc, tok, err := s.Tickets.Issue(claims.Subject, req.CharacterID, srv.ServerID, srv.WorldID, srv.BuildID)
	if err != nil {
		serverError(w, "issue ticket", err)
		return
	}
	if err := s.Store.InsertJoinTicket(r.Context(), store.JoinTicket{
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
		serverError(w, "persist ticket", err)
		return
	}
	writeJSON(w, http.StatusOK, joinResp{
		ServerEndpoint: srv.Endpoint,
		JoinTicket:     tok,
		ExpiresAt:      time.UnixMilli(tc.GetExpiresAtUnixMs()).UTC().Format(time.RFC3339),
	})
}

// --- helpers ----------------------------------------------------------------

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
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	if err := json.NewEncoder(w).Encode(body); err != nil {
		log.Printf("rest: encode response: %v", err)
	}
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

func serverError(w http.ResponseWriter, what string, err error) {
	log.Printf("rest: %s: %v", what, err)
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

// Package metrics は auth / matchmaking 固有の Prometheus 系列を定義する
// （基本設計第13章 / 10B 3.5）。共通系列（HTTP/gRPC/DB latency）は
// services/common/obs 側にある。
package metrics

import (
	"living-world-survival/services/common/obs"

	"github.com/prometheus/client_golang/prometheus"
)

// tickBuckets は DS Tick の分布。Gate は P95≤40ms / P99≤50ms（10B 3.1）なので、
// 0.04 / 0.05 を bucket 境界に含めて補間ではなく実測で判定できるようにする。
//
// float64 は Prometheus の境界の型で、通貨・数量ではない（MVP 13.1 の禁止対象外）。
//
//nolint:forbidigo // Histogram bucket boundaries are seconds, not money.
var tickBuckets = []float64{
	0.005, 0.01, 0.02, 0.03, 0.04, 0.05, 0.075, 0.1, 0.2, 0.5, 1,
}

var (
	// DSTickSeconds は DS が Heartbeat で自報告した tick_ms の分布。
	//
	// 落とし穴（10B 6章）: Tick はハーネス側の RTT ではなく **DS の自報告を正とする**。
	// 計測点をここに一本化するのはそのため。
	DSTickSeconds = prometheus.NewHistogramVec(prometheus.HistogramOpts{
		Name:    "ds_tick_seconds",
		Help:    "Dedicated Server self-reported tick duration, from Heartbeat tick_ms.",
		Buckets: tickBuckets,
	}, []string{"server_id"})

	// DSPlayers は DS ごとの接続数（第13章 接続数）。
	DSPlayers = prometheus.NewGaugeVec(prometheus.GaugeOpts{
		Name: "ds_players",
		Help: "Players currently connected to a Dedicated Server, from Heartbeat.",
	}, []string{"server_id"})

	// DSReady は DS が Matchmaking 対象か（1=ready / 0=not ready）。
	DSReady = prometheus.NewGaugeVec(prometheus.GaugeOpts{
		Name: "ds_ready",
		Help: "Whether a Dedicated Server reports itself ready for matchmaking.",
	}, []string{"server_id"})

	// DSHeartbeats は Heartbeat 受信数。途絶＝DS 停止の検知に使う。
	DSHeartbeats = prometheus.NewCounterVec(prometheus.CounterOpts{
		Name: "ds_heartbeats_total",
		Help: "Heartbeats received from Dedicated Servers.",
	}, []string{"server_id"})

	// LoginAttempts はログイン試行数（result=success/invalid_credentials/rate_limited）。
	// 第16章 Auth 失敗の Rate Limit と MVP-SEC-009 の監査に使う。
	LoginAttempts = prometheus.NewCounterVec(prometheus.CounterOpts{
		Name: "auth_login_attempts_total",
		Help: "Login attempts by result.",
	}, []string{"result"})

	// RateLimited は Rate Limit で弾いた回数（scope=login/account_create/...）。
	RateLimited = prometheus.NewCounterVec(prometheus.CounterOpts{
		Name: "auth_rate_limited_total",
		Help: "Requests rejected by the auth rate limiter.",
	}, []string{"scope"})

	// RefreshRotations は Refresh Token ローテーション結果
	// （result=rotated/reuse_detected）。reuse_detected は family 失効＝RFC 9700
	// の再利用検知が発火した回数で、Security 監視の主系列（MVP-SEC-003）。
	RefreshRotations = prometheus.NewCounterVec(prometheus.CounterOpts{
		Name: "auth_refresh_rotations_total",
		Help: "Refresh token rotation outcomes.",
	}, []string{"result"})

	// JoinTicketRedeems は Join Ticket 引き換え結果（MVP-SEC-004 / 監査 MVP-SEC-009）。
	// result=ok/already_used/expired/server_mismatch/invalid_signature/not_found
	JoinTicketRedeems = prometheus.NewCounterVec(prometheus.CounterOpts{
		Name: "auth_join_ticket_redeems_total",
		Help: "Join ticket redemption outcomes.",
	}, []string{"result"})
)

func init() {
	obs.Registry().MustRegister(
		DSTickSeconds, DSPlayers, DSReady, DSHeartbeats,
		LoginAttempts, RateLimited, RefreshRotations, JoinTicketRedeems,
	)
}

// ObserveTickMS は Heartbeat の tick_ms を秒へ直して記録する。
// tick_ms<=0 は「未計測」を意味するので捨てる（0ms の Tick を分布へ混ぜない）。
//
//nolint:forbidigo // Prometheus observations are float64 seconds, not money.
func ObserveTickMS(serverID string, tickMS int32) {
	if tickMS <= 0 {
		return
	}
	DSTickSeconds.WithLabelValues(serverID).Observe(float64(tickMS) / 1000.0)
}

// SetPlayers は DS ごとの接続数を記録する。Gauge が要求する float64 変換を
// この package に閉じ込め、呼び出し側（ハンドラ）に float64 を漏らさない。
//
//nolint:forbidigo // Prometheus gauges are float64; players is a count, not money.
func SetPlayers(serverID string, players int32) {
	DSPlayers.WithLabelValues(serverID).Set(float64(players))
}

// SetReady は DS が Matchmaking 対象かを 1/0 で記録する。
//
//nolint:forbidigo // Prometheus gauges are float64; this is a boolean flag.
func SetReady(serverID string, ready bool) {
	v := 0.0
	if ready {
		v = 1.0
	}
	DSReady.WithLabelValues(serverID).Set(v)
}

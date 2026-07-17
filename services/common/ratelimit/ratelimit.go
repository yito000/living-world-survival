// Package ratelimit はキー単位のトークンバケットを提供する（MVP-SEC-005 /
// 第16章 Auth 失敗の Rate Limit）。auth のログイン失敗と api の Gameplay
// Command で同じ実装を使う。
//
// プロセス内メモリで完結する。複数レプリカでは各々が独立に数えるため、実効
// 閾値はレプリカ数倍になる。MVP は単一レプリカ前提でこれを許容する（分散
// Rate Limit は Redis 等の共有カウンタが要る）。
package ratelimit

import (
	"sync"
	"time"
)

// Limiter はキーごとにトークンバケットを持つ Rate Limiter。ゼロ値は使えない。
type Limiter struct {
	mu      sync.Mutex
	buckets map[string]*bucket

	// burst は瞬間的に許す最大数、refill は 1 トークンの補充間隔。
	burst  int
	refill time.Duration

	// idleTTL を超えて参照されないバケットは捨てる。捨てないと、キーが
	// account_id や IP の場合にマップが単調増加してリークする。
	idleTTL time.Duration

	now func() time.Time // テスト用に時刻を差し替える
}

type bucket struct {
	tokens   float64 //nolint:forbidigo // Token count for rate limiting, not money.
	lastSeen time.Time
}

// New は「rate 回 / per」の割り当てと burst を持つ Limiter を作る。
// 例: New(5, time.Minute, 5) = 1 分あたり 5 回、瞬間 5 回まで。
func New(rate int, per time.Duration, burst int) *Limiter {
	if rate <= 0 || per <= 0 {
		// 誤設定で「全部拒否」にすると障害になる。無効値は制限なしとして扱う
		// （Allow が常に true を返す）。
		return &Limiter{buckets: nil, now: time.Now}
	}
	if burst <= 0 {
		burst = rate
	}
	return &Limiter{
		buckets: make(map[string]*bucket),
		burst:   burst,
		refill:  per / time.Duration(rate),
		idleTTL: per * 10,
		now:     time.Now,
	}
}

// Disabled は制限なしの Limiter かどうかを返す。
func (l *Limiter) Disabled() bool { return l == nil || l.buckets == nil }

// Allow は key の残トークンを 1 消費して true を返す。枯渇していれば false。
// 「試行そのもの」を制限したいとき（Gameplay Command 等）に使う。
func (l *Limiter) Allow(key string) bool {
	return l.take(key, true)
}

// Peek は消費せずに、いま Allow が通るかどうかだけを返す。
//
// 「失敗だけを数える」Rate Limit（第16章 Auth 失敗）では、まず Peek で
// 遮断済みかを見て、認証に失敗したときだけ Consume する。成功する正規利用者は
// トークンを一切消費しないので、共有 NAT 配下でも巻き込まれない。
func (l *Limiter) Peek(key string) bool {
	return l.take(key, false)
}

// Consume は key のトークンを 1 消費する（結果は問わない）。
func (l *Limiter) Consume(key string) {
	l.take(key, true)
}

// float64 はトークン残量（小数で補充する）であって通貨・数量ではない
// （MVP 13.1 の禁止対象外）。
//
//nolint:forbidigo // Fractional token balance, not money.
func (l *Limiter) take(key string, consume bool) bool {
	if l.Disabled() {
		return true
	}
	l.mu.Lock()
	defer l.mu.Unlock()

	now := l.now()
	b, ok := l.buckets[key]
	if !ok {
		b = &bucket{tokens: float64(l.burst), lastSeen: now}
		l.buckets[key] = b
		l.evictIdleLocked(now)
	} else {
		// 前回からの経過時間ぶんを補充する（上限 burst）。
		b.tokens += float64(now.Sub(b.lastSeen)) / float64(l.refill)
		if b.tokens > float64(l.burst) {
			b.tokens = float64(l.burst)
		}
		b.lastSeen = now
	}

	if b.tokens < 1 {
		return false
	}
	if consume {
		b.tokens--
	}
	return true
}

// evictIdleLocked は idleTTL を超えて未参照のバケットを捨てる。
// Allow の新規キー登録時にだけ走らせる（専用の janitor goroutine を増やさない）。
func (l *Limiter) evictIdleLocked(now time.Time) {
	for k, b := range l.buckets {
		if now.Sub(b.lastSeen) > l.idleTTL {
			delete(l.buckets, k)
		}
	}
}

// Len は保持しているバケット数を返す（リーク検査用）。
func (l *Limiter) Len() int {
	if l.Disabled() {
		return 0
	}
	l.mu.Lock()
	defer l.mu.Unlock()
	return len(l.buckets)
}

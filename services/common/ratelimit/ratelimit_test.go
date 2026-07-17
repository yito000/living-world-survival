package ratelimit

import (
	"sync"
	"testing"
	"time"
)

// atClock は決定的にテストするための差し替え時計。
type atClock struct {
	mu  sync.Mutex
	now time.Time
}

func (c *atClock) Now() time.Time {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.now
}

func (c *atClock) advance(d time.Duration) {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.now = c.now.Add(d)
}

func newTestLimiter(rate int, per time.Duration, burst int) (*Limiter, *atClock) {
	c := &atClock{now: time.Unix(1_700_000_000, 0)}
	l := New(rate, per, burst)
	l.now = c.Now
	return l, c
}

func TestAllowsUpToBurstThenRejects(t *testing.T) {
	l, _ := newTestLimiter(5, time.Minute, 5)

	for i := 0; i < 5; i++ {
		if !l.Allow("k") {
			t.Fatalf("request %d should be allowed within burst", i+1)
		}
	}
	if l.Allow("k") {
		t.Fatal("6th request should be rejected once the bucket is empty")
	}
}

func TestRefillsOverTime(t *testing.T) {
	l, clock := newTestLimiter(5, time.Minute, 5)
	for i := 0; i < 5; i++ {
		l.Allow("k")
	}
	if l.Allow("k") {
		t.Fatal("bucket should be empty")
	}

	// 5回/分 = 12秒で 1 トークン。
	clock.advance(12 * time.Second)
	if !l.Allow("k") {
		t.Fatal("one token should have refilled after 12s")
	}
	if l.Allow("k") {
		t.Fatal("only one token should have refilled")
	}
}

func TestRefillCapsAtBurst(t *testing.T) {
	l, clock := newTestLimiter(5, time.Minute, 5)
	l.Allow("k")

	// 長時間放置しても burst を超えて貯まらない（貯金して一気に撃てない）。
	clock.advance(time.Hour)
	for i := 0; i < 5; i++ {
		if !l.Allow("k") {
			t.Fatalf("request %d should be allowed up to burst", i+1)
		}
	}
	if l.Allow("k") {
		t.Fatal("tokens must cap at burst, not accumulate indefinitely")
	}
}

// キーが独立していないと、1 アカウントの失敗が他人を巻き込む。
func TestKeysAreIndependent(t *testing.T) {
	l, _ := newTestLimiter(2, time.Minute, 2)
	l.Allow("a")
	l.Allow("a")
	if l.Allow("a") {
		t.Fatal("key a should be exhausted")
	}
	if !l.Allow("b") {
		t.Fatal("key b must have its own bucket")
	}
}

// 誤設定で全拒否になると障害になる。無効値は「制限なし」。
func TestInvalidConfigDisablesLimiting(t *testing.T) {
	for _, tc := range []struct {
		rate int
		per  time.Duration
	}{
		{0, time.Minute}, {-1, time.Minute}, {5, 0}, {5, -time.Second},
	} {
		l := New(tc.rate, tc.per, 5)
		if !l.Disabled() {
			t.Errorf("New(%d, %v) should be disabled", tc.rate, tc.per)
		}
		for i := 0; i < 100; i++ {
			if !l.Allow("k") {
				t.Fatalf("disabled limiter must always allow (rate=%d per=%v)", tc.rate, tc.per)
			}
		}
	}
}

func TestNilLimiterAllows(t *testing.T) {
	var l *Limiter
	if !l.Allow("k") {
		t.Fatal("nil limiter must allow (fail open, not closed)")
	}
	if l.Len() != 0 {
		t.Fatal("nil limiter should report no buckets")
	}
}

func TestBurstDefaultsToRate(t *testing.T) {
	l, _ := newTestLimiter(3, time.Minute, 0)
	for i := 0; i < 3; i++ {
		if !l.Allow("k") {
			t.Fatalf("request %d should be allowed (burst defaults to rate)", i+1)
		}
	}
	if l.Allow("k") {
		t.Fatal("4th should be rejected")
	}
}

// キーごとのバケットを捨てないと account_id/IP 単位でマップがリークする。
func TestIdleBucketsAreEvicted(t *testing.T) {
	l, clock := newTestLimiter(5, time.Minute, 5)
	for _, k := range []string{"a", "b", "c"} {
		l.Allow(k)
	}
	if got := l.Len(); got != 3 {
		t.Fatalf("expected 3 buckets, got %d", got)
	}

	// idleTTL = per*10 = 10分。それを超えて放置したものは、次の新規キー登録で捨てる。
	clock.advance(11 * time.Minute)
	l.Allow("fresh")

	if got := l.Len(); got != 1 {
		t.Fatalf("idle buckets should be evicted, got %d buckets", got)
	}
	if !l.Allow("a") {
		t.Fatal("an evicted key should start from a full bucket")
	}
}

func TestConcurrentAllowIsRaceFree(t *testing.T) {
	l := New(1000, time.Minute, 1000)

	var wg sync.WaitGroup
	for i := 0; i < 50; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			for j := 0; j < 20; j++ {
				l.Allow("shared")
			}
		}()
	}
	wg.Wait()

	// 50*20 = 1000 = burst をちょうど使い切る。
	if l.Allow("shared") {
		t.Fatal("bucket should be exactly exhausted after 1000 concurrent requests")
	}
}

// Peek は遮断状態を見るだけで消費しない（成功した正規利用者を巻き込まない）。
func TestPeekDoesNotConsume(t *testing.T) {
	l, _ := newTestLimiter(2, time.Minute, 2)

	for i := 0; i < 10; i++ {
		if !l.Peek("k") {
			t.Fatalf("Peek #%d should stay true: it must not consume tokens", i+1)
		}
	}
	// 消費していないので Allow はまだ burst 回通る。
	for i := 0; i < 2; i++ {
		if !l.Allow("k") {
			t.Fatalf("Allow #%d should pass: Peek must not have consumed the bucket", i+1)
		}
	}
	if l.Peek("k") {
		t.Fatal("Peek should report blocked once the bucket is empty")
	}
}

// 失敗だけを数える運用（第16章 Auth 失敗の Rate Limit）が成立すること。
func TestConsumeOnFailureOnlyPattern(t *testing.T) {
	l, _ := newTestLimiter(3, time.Minute, 3)

	// 失敗を 3 回 → 以降は遮断。
	for i := 0; i < 3; i++ {
		if !l.Peek("ip") {
			t.Fatalf("attempt %d should not be blocked yet", i+1)
		}
		l.Consume("ip")
	}
	if l.Peek("ip") {
		t.Fatal("4th attempt should be blocked after 3 failures")
	}
}

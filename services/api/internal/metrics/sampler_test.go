package metrics

import (
	"context"
	"errors"
	"testing"
	"time"

	"living-world-survival/services/api/internal/store"

	"github.com/prometheus/client_golang/prometheus/testutil"
)

type fakeBacklog struct {
	backlog    store.OutboxBacklog
	backlogErr error

	age    float64 //nolint:forbidigo // Elapsed seconds, not money.
	ageOK  bool
	ageErr error
}

func (f *fakeBacklog) OutboxBacklogStats(context.Context) (store.OutboxBacklog, error) {
	return f.backlog, f.backlogErr
}

//nolint:forbidigo // Elapsed seconds, not money.
func (f *fakeBacklog) NewestEventAgeSeconds(context.Context) (float64, bool, error) {
	return f.age, f.ageOK, f.ageErr
}

func TestSamplerRecordsBacklogAndLag(t *testing.T) {
	s := NewSampler(&fakeBacklog{
		backlog: store.OutboxBacklog{Depth: 7, OldestAgeSec: 12.5},
		age:     3.25, ageOK: true,
	}, time.Second)
	s.sampleOnce(context.Background())

	if got := testutil.ToFloat64(OutboxDepth); got != 7 {
		t.Errorf("outbox_depth = %v, want 7", got)
	}
	if got := testutil.ToFloat64(OutboxOldestAgeSeconds); got != 12.5 {
		t.Errorf("outbox_oldest_age_seconds = %v, want 12.5", got)
	}
	if got := testutil.ToFloat64(EventLagSeconds); got != 3.25 {
		t.Errorf("event_lag_seconds = %v, want 3.25", got)
	}
}

// DB 断でサンプリングが失敗したとき、直前の値を据え置くと「詰まっていない」
// と誤読される。-1 を出して「測れていない」ことを見せる。
func TestSamplerMarksUnknownOnError(t *testing.T) {
	OutboxDepth.Set(42)
	OutboxOldestAgeSeconds.Set(42)
	EventLagSeconds.Set(42)

	s := NewSampler(&fakeBacklog{
		backlogErr: errors.New("connection refused"),
		ageErr:     errors.New("connection refused"),
	}, time.Second)
	s.sampleOnce(context.Background())

	if got := testutil.ToFloat64(OutboxDepth); got != -1 {
		t.Errorf("outbox_depth = %v, want -1 on sample failure", got)
	}
	if got := testutil.ToFloat64(OutboxOldestAgeSeconds); got != -1 {
		t.Errorf("outbox_oldest_age_seconds = %v, want -1 on sample failure", got)
	}
	if got := testutil.ToFloat64(EventLagSeconds); got != -1 {
		t.Errorf("event_lag_seconds = %v, want -1 on sample failure", got)
	}
}

// イベントが 1 件も無い間に 0 を載せると「たった今処理した」に見える。
func TestSamplerLeavesLagUntouchedWhenNoEvents(t *testing.T) {
	EventLagSeconds.Set(99)

	s := NewSampler(&fakeBacklog{ageOK: false}, time.Second)
	s.sampleOnce(context.Background())

	if got := testutil.ToFloat64(EventLagSeconds); got != 99 {
		t.Errorf("event_lag_seconds = %v, want it left at 99 when there are no events", got)
	}
}

func TestNewSamplerDefaultsInterval(t *testing.T) {
	if got := NewSampler(&fakeBacklog{}, 0).Interval; got != 5*time.Second {
		t.Errorf("interval = %v, want the 5s default", got)
	}
	if got := NewSampler(&fakeBacklog{}, -1).Interval; got != 5*time.Second {
		t.Errorf("negative interval should fall back to the default, got %v", got)
	}
}

// Run は ctx の終了で必ず戻る（サービス停止をブロックしない）。
func TestSamplerRunStopsOnContextCancel(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan struct{})
	go func() {
		NewSampler(&fakeBacklog{ageOK: true}, 10*time.Millisecond).Run(ctx)
		close(done)
	}()

	cancel()
	select {
	case <-done:
	case <-time.After(2 * time.Second):
		t.Fatal("Run did not return after ctx cancel")
	}
}

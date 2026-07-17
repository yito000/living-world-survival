package metrics

import (
	"context"
	"time"

	"living-world-survival/services/api/internal/store"
	"living-world-survival/services/common/obs"
)

// Backlog は sampler が必要とする store の部分集合（テストで差し替えるため）。
//
// float64 は「経過秒数」であって通貨・数量ではない（MVP 13.1 の禁止対象外）。
//
//nolint:forbidigo // Elapsed seconds for metrics, not money.
type Backlog interface {
	OutboxBacklogStats(ctx context.Context) (store.OutboxBacklog, error)
	NewestEventAgeSeconds(ctx context.Context) (age float64, ok bool, err error)
}

// Sampler は「スクレイプ時に DB を叩く」代わりに、一定間隔で Gauge を更新する。
//
// GaugeFunc で /metrics のたびにクエリを投げると、スクレイパの数と間隔がそのまま
// DB 負荷になり、負荷試験中に計測自身がボトルネックを作る。
type Sampler struct {
	Store    Backlog
	Interval time.Duration
}

// NewSampler は既定 5 秒間隔の Sampler を作る。Soak の記録間隔（60秒）より
// 十分細かく、かつ DB 負荷にならない範囲。
func NewSampler(st Backlog, interval time.Duration) *Sampler {
	if interval <= 0 {
		interval = 5 * time.Second
	}
	return &Sampler{Store: st, Interval: interval}
}

// Run は ctx が終わるまで Gauge を更新し続ける。
func (s *Sampler) Run(ctx context.Context) {
	ticker := time.NewTicker(s.Interval)
	defer ticker.Stop()

	s.sampleOnce(ctx)
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			s.sampleOnce(ctx)
		}
	}
}

//nolint:forbidigo // Prometheus gauges are float64; these are counts/seconds, not money.
func (s *Sampler) sampleOnce(ctx context.Context) {
	// サンプリングが DB 復旧待ちで固まらないよう、1 周期より短い上限を切る。
	qctx, cancel := context.WithTimeout(ctx, 3*time.Second)
	defer cancel()

	if b, err := s.Store.OutboxBacklogStats(qctx); err != nil {
		// DB 断でサンプリングが失敗しても、古い値を残さない。Gauge を据え置くと
		// 「詰まっていないように見える」誤読を生む（10B 6章 Outbox 滞留 vs リーク）。
		OutboxDepth.Set(-1)
		OutboxOldestAgeSeconds.Set(-1)
		obs.L(ctx).Warn("outbox backlog sample failed", "error", err.Error())
	} else {
		OutboxDepth.Set(float64(b.Depth))
		OutboxOldestAgeSeconds.Set(b.OldestAgeSec)
	}

	if age, ok, err := s.Store.NewestEventAgeSeconds(qctx); err != nil {
		EventLagSeconds.Set(-1)
		obs.L(ctx).Warn("event lag sample failed", "error", err.Error())
	} else if ok {
		EventLagSeconds.Set(age)
	}
	// イベントが 1 件も無い間は EventLagSeconds を触らない（0 だと
	// 「たった今処理した」に見えてしまう）。
}

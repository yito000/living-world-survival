// Package ranking computes the periodic asset ranking (MVP 12.3 / 09B 3.9):
// net_worth = 現金 + Item評価額 + 設備評価額 for every Character/AI, stored with
// the price_version it was valued at. All arithmetic is integer (13.1).
//
// The batch can be heavy, so it never runs inside a request handler: it is
// driven by a ticker goroutine or the internal admin endpoint, and one run at a
// time (BSD R7 / MVP 10.2 の非同期方針).
package ranking

import (
	"context"
	"fmt"
	"sync"
	"time"

	"living-world-survival/services/api/internal/store"
	"living-world-survival/services/common/obs"
)

// DefaultInterval is how often the batch runs unattended (09B 3.9: 1時間ごと).
const DefaultInterval = time.Hour

// Batch runs the ranking calculation.
type Batch struct {
	store *store.Store

	// mu makes a run exclusive: a ticker tick that lands while an admin-triggered
	// run is still going is skipped rather than queued, so a slow run cannot pile
	// up duplicate price_versions.
	mu      sync.Mutex
	running bool
}

// New returns a Batch over st.
func New(st *store.Store) *Batch { return &Batch{store: st} }

// Result summarises one completed run.
type Result struct {
	PriceVersion int64
	OwnerCount   int
}

// ErrAlreadyRunning is returned by Run when a run is already in progress.
var ErrAlreadyRunning = fmt.Errorf("ranking: a run is already in progress")

// Run executes one ranking run: compute every owner's net worth, then persist it
// under a fresh price_version. Returns ErrAlreadyRunning if one is in flight.
func (b *Batch) Run(ctx context.Context) (Result, error) {
	b.mu.Lock()
	if b.running {
		b.mu.Unlock()
		return Result{}, ErrAlreadyRunning
	}
	b.running = true
	b.mu.Unlock()
	defer func() {
		b.mu.Lock()
		b.running = false
		b.mu.Unlock()
	}()

	entries, err := b.store.ComputeNetWorth(ctx)
	if err != nil {
		return Result{}, err
	}
	// SaveRanking allocates price_version inside its own tx: the mutex above only
	// serializes runs within this process, not across apid replicas.
	version, err := b.store.SaveRanking(ctx, entries)
	if err != nil {
		return Result{}, err
	}
	return Result{PriceVersion: version, OwnerCount: len(entries)}, nil
}

// RunPeriodically runs the batch every interval until ctx is cancelled. It does
// not run immediately on start: startup is already busy and the first ranking is
// not time-critical.
func (b *Batch) RunPeriodically(ctx context.Context, interval time.Duration) {
	if interval <= 0 {
		interval = DefaultInterval
	}
	ticker := time.NewTicker(interval)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			res, err := b.Run(ctx)
			if err != nil {
				// A failed run is not fatal: the next tick retries.
				obs.L(ctx).Warn("ranking run failed", "error", err.Error())
				continue
			}
			obs.L(ctx).Info("ranking run complete",
				"price_version", res.PriceVersion, "owners", res.OwnerCount)
		}
	}
}

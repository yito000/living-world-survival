package main

import (
	"context"
	"errors"
	"fmt"
	"math/rand"
	"time"

	"google.golang.org/grpc/metadata"

	survivalv1 "living-world-survival/services/gen/go/survival/v1"
)

// syntheticDS は Matchmaking から見た 1 台の Dedicated Server を演じる。
//
// 本物の DS は Unity/FishNet であり Go からは喋れない（package doc 参照）。ここで模擬するのは
// **DS が auth に対して行う Matchmaking 側の振る舞い**（RegisterServer / 1Hz Heartbeat /
// RedeemJoinTicket / MarkDraining）だけである。
//
// tickMS の扱い（10B 6章「Tick 計測の出所を DS に統一」）:
//   - tickMS > 0: Heartbeat に **合成値** を載せる。実 DS が居ないときの穴埋めであって計測値ではない。
//     report の tick_source は "synthetic" になり、load_assert.py は Gate を PASS にしない。
//   - tickMS <= 0: Heartbeat から tick_ms を落とす（auth 側が tick_ms<=0 を破棄する）。実 DS 併走時は
//     こちらにして、ds_tick_seconds に実 DS の自報告値だけが載るようにする。
type syntheticDS struct {
	mm       survivalv1.MatchmakingServiceClient
	secret   string
	serverID string
	worldID  string
	buildID  string
	tickMS   int
	jitterMS int
	players  int
	stats    *stats
}

func (d *syntheticDS) ctx(ctx context.Context) context.Context {
	return withSecret(ctx, d.secret)
}

func (d *syntheticDS) register(ctx context.Context) error {
	resp, err := d.mm.RegisterServer(d.ctx(ctx), &survivalv1.RegisterServerRequest{
		ServerId: d.serverID,
		WorldId:  d.worldID,
		BuildId:  d.buildID,
		Endpoint: "127.0.0.1:7777",
		Capacity: int32(maxInt(d.players, 1)),
	})
	if err != nil {
		return err
	}
	if !resp.GetOk() {
		return errors.New("RegisterServer が ok=false を返した")
	}
	// Matchmaking の候補になるには ready な Heartbeat が 1 回要る。
	return d.heartbeat(ctx)
}

// heartbeat は 1 回分の Heartbeat を送る。
func (d *syntheticDS) heartbeat(ctx context.Context) error {
	start := time.Now()
	resp, err := d.mm.Heartbeat(d.ctx(ctx), &survivalv1.HeartbeatRequest{
		ServerId: d.serverID,
		Players:  int32(d.players),
		Ready:    true,
		TickMs:   int32(d.nextTickMS()),
	})
	elapsed := time.Since(start)
	switch {
	case err != nil:
		d.stats.record("ds_heartbeat", elapsed, "error", err)
		return err
	case !resp.GetOk():
		err := errors.New("Heartbeat が ok=false を返した")
		d.stats.record("ds_heartbeat", elapsed, "error", err)
		return err
	}
	d.stats.record("ds_heartbeat", elapsed, "ok", nil)
	return nil
}

// nextTickMS は合成 tick_ms を返す。tickMS<=0 のときは 0 を返し、auth 側で破棄させる
// （＝実 DS の tick だけを計測したいモード）。
func (d *syntheticDS) nextTickMS() int {
	if d.tickMS <= 0 {
		return 0
	}
	if d.jitterMS <= 0 {
		return d.tickMS
	}
	// 20Hz 目標の DS が実際に見せる程度のゆらぎを与える（±jitter）。
	v := d.tickMS + rand.Intn(2*d.jitterMS+1) - d.jitterMS //nolint:gosec // 負荷生成のゆらぎで暗号用途ではない
	if v < 1 {
		v = 1
	}
	return v
}

// heartbeatLoop は約 1Hz で Heartbeat を送り続ける（実 DS の Heartbeat 周期に合わせる）。
func (d *syntheticDS) heartbeatLoop(ctx context.Context) {
	t := time.NewTicker(1 * time.Second)
	defer t.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-t.C:
			hbCtx, cancel := context.WithTimeout(context.WithoutCancel(ctx), 3*time.Second)
			if err := d.heartbeat(hbCtx); err != nil {
				// 単発の失敗で負荷を止めない（記録は stats に残る）。
				_ = err
			}
			cancel()
		}
	}
}

func (d *syntheticDS) redeem(ctx context.Context, ticket string) error {
	resp, err := d.mm.RedeemJoinTicket(d.ctx(ctx), &survivalv1.RedeemJoinTicketRequest{
		ServerId: d.serverID,
		Ticket:   ticket,
	})
	if err != nil {
		return err
	}
	if !resp.GetOk() {
		return fmt.Errorf("RedeemJoinTicket 拒否: %s", resp.GetError())
	}
	return nil
}

func (d *syntheticDS) markDraining(ctx context.Context) error {
	resp, err := d.mm.MarkDraining(d.ctx(ctx), &survivalv1.MarkDrainingRequest{ServerId: d.serverID})
	if err != nil {
		return err
	}
	if !resp.GetOk() {
		return errors.New("MarkDraining が ok=false を返した")
	}
	return nil
}

// withSecret は内部 gRPC の共有秘密（x-service-secret）を metadata に載せる。
// 空（dev 既定）なら何もしない。
func withSecret(ctx context.Context, secret string) context.Context {
	if secret == "" {
		return ctx
	}
	return metadata.AppendToOutgoingContext(ctx, "x-service-secret", secret)
}

func maxInt(a, b int) int {
	if a > b {
		return a
	}
	return b
}

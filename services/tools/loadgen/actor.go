package main

import (
	"context"
	"encoding/json"
	"fmt"
	"math/rand"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"

	survivalv1 "living-world-survival/services/gen/go/survival/v1"
)

// actor は 1 体の行動主体（player / ai / animal）を回す。
//
// 送るのは **DS が生成する Domain Event の形をした入力だけ**である（06A/06B 0.3）。
// 在庫・通貨・WorldItem への反映は API が単一 Writer として行い、ドライバは結果を受け取るだけ。
// Damage/Loot/価格をドライバ側で確定させないこと（MVP-SEC-005/006 / 10B 6章）。
type actor struct {
	kind     string // player / ai / animal
	name     string
	worldID  string
	actorID  string // gameplay の aggregate_id 兼 owner
	buyerFor string // 購入者 id（下の注記参照）
	wd       survivalv1.WorldDataServiceClient
	econ     survivalv1.EconomyServiceClient
	pool     *pgxpool.Pool
	secret   string
	stats    *stats
	interval time.Duration
	buys     bool

	localSeq int64

	// 購入側の状態
	buyerInstanceID string
	stock           []*survivalv1.BuyerStockEntry
	invVersion      int64
	purchases       int
}

// 採取で扱う資源。実在する Item Definition（migration 0002 seed）を使う。
// どれを掘れるかは本来 DS のワールド配置が決めるので、ここは「掘れた結果」を模した
// 入力を出しているだけであり、ドライバが Loot を決めているわけではない。
const loadItemDef = "stone"

// seedBalance は購入者に初期残高を積む。実運用では運営/初期化が入れる行で、
// 計測対象の RPC ではない（DB 直挿しなのはそのため）。
func (a *actor) seedBalance(ctx context.Context) error {
	if !a.buys {
		return nil
	}
	return creditBalance(ctx, a.pool, a.buyerFor)
}

func creditBalance(ctx context.Context, pool *pgxpool.Pool, ownerID string) error {
	_, err := pool.Exec(ctx,
		`INSERT INTO currency_ledger (entry_id, owner_id, delta, balance_after, reason)
		 VALUES ($1, $2, $3, $3, 'loadgen')`,
		newUUID(), ownerID, startingBalance)
	return err
}

// run は player / ai の定常ループ: 採取 → 使用 → Pickup 相当 → （時々）購入。
func (a *actor) run(ctx context.Context) {
	t := time.NewTicker(a.interval)
	defer t.Stop()
	purchaseEvery := 5 // interval の 5 周期に 1 回購入する
	i := 0
	for {
		select {
		case <-ctx.Done():
			return
		case <-t.C:
			a.gather(ctx)
			a.use(ctx)
			a.pickup(ctx)
			if a.buys && i%purchaseEvery == 0 {
				a.purchase(ctx)
			}
			i++
		}
	}
}

// runAnimal は動物の負荷。動物の本体負荷（NavMesh/AI/描画）は DS 側＝10A の担当で、
// バックエンドから見えるのは狩猟イベントの発生分だけ。ここではそれだけを出す。
func (a *actor) runAnimal(ctx context.Context) {
	t := time.NewTicker(a.interval)
	defer t.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-t.C:
			// hunting.animal_killed は記録 + NATS のみ（在庫効果なし）。
			a.appendEvent(ctx, "gameplay_animal", "hunting.animal_killed", map[string]any{
				"animal_id": a.actorID,
				"world_id":  a.worldID,
			})
		}
	}
}

// gather は採取（resource.mined）。付与内容は API が在庫へ反映する。
func (a *actor) gather(ctx context.Context) {
	a.appendEvent(ctx, "gameplay_gather", "resource.mined", map[string]any{
		"actor_id": a.actorID,
		"grants":   []map[string]any{{"item_definition_id": loadItemDef, "quantity": 2}},
	})
}

// use は使用（inventory.item_consumed）。gather が 2 個付与して use/pickup で 2 個減るので、
// 1 周期で収支ゼロ。長時間回しても在庫が枯れる/満杯になるノイズが乗らない。
func (a *actor) use(ctx context.Context) {
	a.appendEvent(ctx, "gameplay_use", "inventory.item_consumed", map[string]any{
		"actor_id":           a.actorID,
		"item_definition_id": loadItemDef,
		"quantity":           1,
	})
}

// pickup は「Pickup 相当」。API 側に pickup 専用イベントは無く、WorldItem を作る経路は
// item.discarded（Drop）なので、WorldItem テーブルを触るバックエンド負荷はこれで代表させる。
// Client→DS の Pickup 操作そのものは 10A の担当（package doc のスコープ参照）。
func (a *actor) pickup(ctx context.Context) {
	a.appendEvent(ctx, "gameplay_pickup", "item.discarded", map[string]any{
		"actor_id":           a.actorID,
		"item_definition_id": loadItemDef,
		"quantity":           1,
		"world_item_id":      newUUID(),
		"position": map[string]any{
			"x": rand.Intn(200) - 100, //nolint:gosec // 配置のばらつきで暗号用途ではない
			"y": 0,
			"z": rand.Intn(200) - 100, //nolint:gosec // 同上
		},
		"tags": []string{"loadgen"},
	})
}

// appendEvent は 1 イベントを AppendEvents で送る。「通常操作 P95≤200ms」の Gate は
// この RPC のサーバー側計測（grpc_server_handling_seconds{method="AppendEvents"}）で判定する。
func (a *actor) appendEvent(ctx context.Context, kind, typ string, payload map[string]any) {
	raw, err := json.Marshal(payload)
	if err != nil {
		a.stats.record(kind, 0, "error", err)
		return
	}
	a.localSeq++
	ev := &survivalv1.DomainEvent{
		EventId:          "ev-" + newUUID(),
		WorldId:          a.worldID,
		AggregateId:      a.actorID,
		LocalSequence:    a.localSeq,
		Type:             typ,
		Payload:          raw,
		OccurredAtUnixMs: time.Now().UnixMilli(),
	}
	opCtx, cancel := context.WithTimeout(withSecret(ctx, a.secret), 15*time.Second)
	defer cancel()
	start := time.Now()
	resp, err := a.wd.AppendEvents(opCtx, &survivalv1.AppendEventsRequest{
		ServerId: "loadgen",
		Events:   []*survivalv1.DomainEvent{ev},
	})
	elapsed := time.Since(start)
	switch {
	case err != nil:
		a.stats.record(kind, elapsed, "error", err)
	case len(resp.GetResults()) == 0:
		a.stats.record(kind, elapsed, "error", fmt.Errorf("AppendEvents が結果を返さなかった"))
	case resp.GetResults()[0] == survivalv1.ResultStatus_RESULT_STATUS_OK,
		resp.GetResults()[0] == survivalv1.ResultStatus_RESULT_STATUS_DUPLICATE:
		a.stats.record(kind, elapsed, "ok", nil)
	default:
		a.stats.record(kind, elapsed, "error", fmt.Errorf("AppendEvents %s: %v", typ, resp.GetResults()[0]))
	}
}

// ensureBuyer は Buyer を（必要なら再）登録する。在庫は API が seed から決定的に生成し、
// unit_price も **API が確定させる**（ドライバは価格を持たない / MVP-SEC-006）。
func (a *actor) ensureBuyer(ctx context.Context) bool {
	if a.buyerInstanceID != "" && len(a.stock) > 0 {
		return true
	}
	opCtx, cancel := context.WithTimeout(withSecret(ctx, a.secret), 15*time.Second)
	defer cancel()
	start := time.Now()
	resp, err := a.econ.RegisterBuyer(opCtx, &survivalv1.RegisterBuyerRequest{
		IdempotencyKey:   "loadgen-buyer-" + newUUID(),
		WorldId:          a.worldID,
		RegionId:         "region-1",
		Seed:             rand.Int63(), //nolint:gosec // 在庫生成 seed。暗号用途ではない
		InventoryTableId: envOr("LOADGEN_BUYER_TABLE", "general_goods_buyer_v1"),
		PriceModifierBp:  10000,
		SpawnAtUnixMs:    time.Now().UnixMilli(),
		DespawnAtUnixMs:  time.Now().Add(1 * time.Hour).UnixMilli(),
	})
	elapsed := time.Since(start)
	if err != nil {
		a.stats.record("economy_register_buyer", elapsed, "error", err)
		return false
	}
	a.stats.record("economy_register_buyer", elapsed, "ok", nil)
	a.buyerInstanceID = resp.GetBuyerInstanceId()
	a.stock = resp.GetStock()
	return len(a.stock) > 0
}

// purchase は購入 1 回。「購入 P95≤500ms（DB commit 含む）」の Gate は
// grpc_server_handling_seconds{method="CommitPurchase"} で判定する（ここの RTT ではない）。
//
// 価格はサーバーが決める。ドライバは stock_entry_id を指すだけで、いくら課金されたかは
// レスポンス（charged）を受け取るのみ。金額を送りつけたり検算したりしない。
func (a *actor) purchase(ctx context.Context) {
	if !a.ensureBuyer(ctx) {
		return
	}
	entry := a.stock[a.purchases%len(a.stock)]

	opCtx, cancel := context.WithTimeout(withSecret(ctx, a.secret), 15*time.Second)
	defer cancel()
	start := time.Now()
	resp, err := a.econ.CommitPurchase(opCtx, &survivalv1.CommitPurchaseRequest{
		IdempotencyKey:   "loadgen-buy-" + newUUID(), // 毎回新しい鍵（再送試験は m6check の担当）
		BuyerInstanceId:  a.buyerInstanceID,
		StockEntryId:     entry.GetStockEntryId(),
		PurchaserId:      a.buyerFor,
		InventoryVersion: a.invVersion,
	})
	elapsed := time.Since(start)
	if err != nil {
		a.stats.record("economy_purchase", elapsed, "error", err)
		return
	}
	a.purchases++

	switch resp.GetStatus() {
	case survivalv1.PurchaseStatus_PURCHASE_STATUS_COMMITTED:
		a.invVersion = resp.GetNewPersistedInventoryVersion()
		a.stats.record("economy_purchase", elapsed, "ok", nil)
	case survivalv1.PurchaseStatus_PURCHASE_STATUS_OUT_OF_STOCK:
		// この在庫枠は尽きた。枠を落とし、全部尽きたら次の Buyer を登録する。
		a.dropStock(entry.GetStockEntryId())
		a.stats.record("economy_purchase", elapsed, "rejected", nil)
	case survivalv1.PurchaseStatus_PURCHASE_STATUS_REJECTED:
		// 満杯インベントリ or version 不一致。実 DS なら消費/売却で空くが、本ドライバの
		// 目的は購入経路の DB commit を測ることなので、購入者を入れ替えて計測を続ける。
		a.rotatePurchaser(ctx)
		a.stats.record("economy_purchase", elapsed, "rejected", nil)
	default:
		a.stats.record("economy_purchase", elapsed, "rejected", nil)
	}
}

func (a *actor) dropStock(stockEntryID string) {
	out := a.stock[:0]
	for _, s := range a.stock {
		if s.GetStockEntryId() != stockEntryID {
			out = append(out, s)
		}
	}
	a.stock = out
	if len(a.stock) == 0 {
		a.buyerInstanceID = ""
	}
}

// rotatePurchaser は購入者 id を差し替える。残高投入は DB 直挿し（初期化相当）。
func (a *actor) rotatePurchaser(ctx context.Context) {
	if a.pool == nil {
		return
	}
	newID := newUUID()
	seedCtx, cancel := context.WithTimeout(context.WithoutCancel(ctx), 5*time.Second)
	defer cancel()
	if err := creditBalance(seedCtx, a.pool, newID); err != nil {
		return
	}
	a.buyerFor = newID
	a.invVersion = 0
}

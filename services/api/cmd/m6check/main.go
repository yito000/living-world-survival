// M6 疎通確認: 実 apid コンテナ(:8092) の EconomyService を叩き、
// RegisterBuyer → CommitPurchase → 冪等再送 → DespawnBuyer と NATS 発行を確かめる。
package main

import (
	"context"
	"encoding/json"
	"fmt"
	"log"
	"os"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/nats-io/nats.go"
	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials/insecure"

	"living-world-survival/services/api/internal/store"
	survivalv1 "living-world-survival/services/gen/go/survival/v1"
)

func main() {
	ctx := context.Background()

	// world / purchaser を用意（購入には残高が要る）。
	pool, err := pgxpool.New(ctx, "postgres://survival:survival@localhost:5432/survival?sslmode=disable")
	must(err, "pg connect")
	defer pool.Close()

	worldID := store.NewUUID()
	_, err = pool.Exec(ctx, `INSERT INTO worlds (world_id) VALUES ($1)`, worldID)
	must(err, "insert world")

	purchaser := store.NewUUID()
	_, err = pool.Exec(ctx,
		`INSERT INTO currency_ledger (entry_id, owner_id, delta, balance_after, reason)
		 VALUES ($1, $2, 100000, 100000, 'm6check')`, store.NewUUID(), purchaser)
	must(err, "credit purchaser")

	// NATS を先に購読して経済イベントを待ち受ける。
	nc, err := nats.Connect("nats://localhost:4222")
	must(err, "nats connect")
	defer nc.Close()
	sub, err := nc.SubscribeSync("world." + worldID + ".event.economy")
	must(err, "subscribe")

	conn, err := grpc.NewClient("localhost:8092", grpc.WithTransportCredentials(insecure.NewCredentials()))
	must(err, "grpc dial")
	defer conn.Close() //nolint:errcheck // 疎通確認ツール。終了時の close 失敗は結果に影響しない
	cli := survivalv1.NewEconomyServiceClient(conn)

	// 1) RegisterBuyer（在庫は seed から決定的に生成される）
	reg, err := cli.RegisterBuyer(ctx, &survivalv1.RegisterBuyerRequest{
		IdempotencyKey:   "m6check-" + store.NewUUID(),
		WorldId:          worldID,
		RegionId:         "region-1",
		Seed:             20260717,
		InventoryTableId: "rare_weapon_buyer_v1",
		PriceModifierBp:  10000,
		SpawnAtUnixMs:    time.Now().UnixMilli(),
		DespawnAtUnixMs:  time.Now().Add(10 * time.Minute).UnixMilli(),
	})
	must(err, "RegisterBuyer")
	fmt.Printf("RegisterBuyer OK: buyer=%s stock=%d\n", reg.GetBuyerInstanceId(), len(reg.GetStock()))
	for _, s := range reg.GetStock() {
		fmt.Printf("  - %-20s unit_price=%-6d remaining=%d\n", s.GetItemDefinitionId(), s.GetUnitPrice(), s.GetRemainingQuantity())
	}

	// 2) CommitPurchase
	entry := reg.GetStock()[0]
	key := "m6check-buy-" + store.NewUUID()
	buy, err := cli.CommitPurchase(ctx, &survivalv1.CommitPurchaseRequest{
		IdempotencyKey:   key,
		BuyerInstanceId:  reg.GetBuyerInstanceId(),
		StockEntryId:     entry.GetStockEntryId(),
		PurchaserId:      purchaser,
		InventoryVersion: 0,
	})
	must(err, "CommitPurchase")
	fmt.Printf("CommitPurchase: status=%v charged=%d new_inv_version=%d instances=%v\n",
		buy.GetStatus(), buy.GetCharged().GetAmount(), buy.GetNewPersistedInventoryVersion(), buy.GetItemInstanceIds())
	if buy.GetStatus() != survivalv1.PurchaseStatus_PURCHASE_STATUS_COMMITTED {
		fail("購入が COMMITTED にならなかった")
	}

	// 3) 同一キー再送 → DUPLICATE（二重確定しない）
	dup, err := cli.CommitPurchase(ctx, &survivalv1.CommitPurchaseRequest{
		IdempotencyKey:   key,
		BuyerInstanceId:  reg.GetBuyerInstanceId(),
		StockEntryId:     entry.GetStockEntryId(),
		PurchaserId:      purchaser,
		InventoryVersion: 0,
	})
	must(err, "CommitPurchase(再送)")
	fmt.Printf("CommitPurchase(再送): status=%v charged=%d\n", dup.GetStatus(), dup.GetCharged().GetAmount())
	if dup.GetStatus() != survivalv1.PurchaseStatus_PURCHASE_STATUS_DUPLICATE {
		fail("再送が DUPLICATE にならなかった")
	}

	var balance int64
	must(pool.QueryRow(ctx,
		`SELECT balance_after FROM currency_ledger WHERE owner_id=$1 ORDER BY created_at DESC LIMIT 1`,
		purchaser).Scan(&balance), "read balance")
	if want := 100000 - buy.GetCharged().GetAmount(); balance != want {
		fail(fmt.Sprintf("残高 %d != %d（二重課金）", balance, want))
	}
	fmt.Printf("残高: 100000 → %d（1回分のみ）\n", balance)

	// 4) DespawnBuyer
	dr, err := cli.DespawnBuyer(ctx, &survivalv1.DespawnBuyerRequest{
		BuyerInstanceId: reg.GetBuyerInstanceId(),
		TargetStatus:    "DESPAWNED",
	})
	must(err, "DespawnBuyer")
	fmt.Printf("DespawnBuyer: status=%v\n", dr.GetStatus())

	// 5) Outbox relay が NATS へ流したか
	fmt.Println("NATS world." + worldID + ".event.economy:")
	got := map[string]bool{}
	for i := 0; i < 3; i++ {
		msg, err := sub.NextMsg(10 * time.Second)
		if err != nil {
			break
		}
		fmt.Printf("  %s\n", string(msg.Data))
		// payload は jsonb 経由で整形が変わる（"type": "x"）ため、部分文字列ではなく
		// JSON として読む。
		var env struct {
			Type string `json:"type"`
		}
		if err := json.Unmarshal(msg.Data, &env); err != nil {
			fail("payload が JSON として読めない: " + err.Error())
		}
		got[env.Type] = true
	}
	for _, t := range []string{"buyer_spawned", "purchase", "buyer_despawned"} {
		if !got[t] {
			fail("NATS に " + t + " が届かなかった")
		}
	}
	fmt.Println("\nM6 疎通確認: 全 OK")
}

func must(err error, what string) {
	if err != nil {
		log.Fatalf("NG: %s: %v", what, err)
	}
}

func fail(msg string) {
	fmt.Fprintln(os.Stderr, "NG: "+msg)
	os.Exit(1)
}

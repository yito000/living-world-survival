package main

import (
	"context"

	"github.com/jackc/pgx/v5/pgxpool"
)

// countBuyers は世界の buyer_instances 行数を数える（Buyer は API 永続なので DS crash に耐える）。
func countBuyers(ctx context.Context, pool *pgxpool.Pool, worldID string) int {
	var n int
	must(pool.QueryRow(ctx, `SELECT count(*) FROM buyer_instances WHERE world_id = $1`, worldID).Scan(&n), "count buyers")
	return n
}

// latestBalance は owner の最新 currency_ledger.balance_after（無ければ 0）。
func latestBalance(ctx context.Context, pool *pgxpool.Pool, ownerID string) int64 {
	var bal int64
	must(pool.QueryRow(ctx,
		`SELECT coalesce((SELECT balance_after FROM currency_ledger
		   WHERE owner_id = $1 ORDER BY created_at DESC, entry_id DESC LIMIT 1), 0)`,
		ownerID).Scan(&bal), "latest balance")
	return bal
}

// purchaseRowCount は冪等キーに対応する purchase_transactions 行数（二重確定なら >1）。
func purchaseRowCount(ctx context.Context, pool *pgxpool.Pool, key string) int {
	var n int
	must(pool.QueryRow(ctx,
		`SELECT count(*) FROM purchase_transactions WHERE idempotency_key = $1`, key).Scan(&n),
		"count purchases")
	return n
}

// debitCountFor は購入者に対する負の（購入）ledger 明細の件数（二重付与検知用）。
func debitCountForAmount(ctx context.Context, pool *pgxpool.Pool, ownerID string, amount int64) int {
	var n int
	must(pool.QueryRow(ctx,
		`SELECT count(*) FROM currency_ledger WHERE owner_id = $1 AND delta = $2`,
		ownerID, -amount).Scan(&n), "count debits")
	return n
}

// itemPersisted は購入で得た item_instance が inventory_entries から参照されているか。
func itemPersisted(ctx context.Context, pool *pgxpool.Pool, instanceID string) bool {
	var ok bool
	must(pool.QueryRow(ctx,
		`SELECT EXISTS(
		   SELECT 1 FROM inventory_entries e
		   JOIN item_instances i ON i.item_instance_id = e.item_instance_id
		   WHERE e.item_instance_id = $1)`, instanceID).Scan(&ok), "item persisted")
	return ok
}

// unpublishedForWorld は world のイベント subject で未 publish の outbox 件数。
func unpublishedForWorld(ctx context.Context, pool *pgxpool.Pool, worldID string) int {
	var n int
	must(pool.QueryRow(ctx,
		`SELECT count(*) FROM outbox_messages
		   WHERE topic LIKE 'world.'||$1||'.event.%' AND published_at IS NULL`,
		worldID).Scan(&n), "count unpublished")
	return n
}

// seedBalance は購入者に初期残高を与える（DS/運営の初期化の代役）。
func seedBalance(ctx context.Context, pool *pgxpool.Pool, ownerID string, amount int64) {
	_, err := pool.Exec(ctx,
		`INSERT INTO currency_ledger (entry_id, owner_id, delta, balance_after, reason)
		 VALUES ($1, $2, $3, $3, 'recoverygen-seed')`, newUUID(), ownerID, amount)
	must(err, "seed balance")
}

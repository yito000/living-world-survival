package store

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"time"

	"github.com/jackc/pgx/v5"
)

// This file is the M6 Economy persistence layer (09B 3.6/3.7). The API is the
// single authoritative Writer: a purchase locks the stock row, verifies, then
// decrements stock and writes ledger/instance/inventory/transaction rows inside
// ONE transaction, enqueueing the NATS economy event via the transactional
// outbox (12.2 / 12.2.1). The DS only mirrors the result — it never re-persists.
//
// Money is BIGINT throughout; no floats (13.1).

// Buyer lifecycle statuses (09B 3.3). Only 'active' accepts new purchases:
// 'preparing' is the despawn hand-off window where started transactions may
// finish but new ones are rejected.
const (
	BuyerStatusActive    = "active"
	BuyerStatusPreparing = "preparing"
	BuyerStatusDespawned = "despawned"
)

// PurchaseOutcome is the result of CommitPurchase, mapped to PurchaseStatus by
// the gRPC layer. It is also the value stored in purchase_transactions.status.
type PurchaseOutcome string

const (
	// PurchaseCommitted means stock/ledger/inventory were durably changed.
	PurchaseCommitted PurchaseOutcome = "committed"
	// PurchaseDuplicate means the idempotency_key replayed; nothing changed and
	// the original result is returned (AT-019).
	PurchaseDuplicate PurchaseOutcome = "duplicate"
	// PurchaseOutOfStock means remaining_quantity was exhausted.
	PurchaseOutOfStock PurchaseOutcome = "out_of_stock"
	// PurchaseInsufficientFunds means balance - unit_price < 0.
	PurchaseInsufficientFunds PurchaseOutcome = "insufficient_funds"
	// PurchaseRejected covers unknown stock, non-active buyer, inventory version
	// mismatch and a full inventory.
	PurchaseRejected PurchaseOutcome = "rejected"
	// saleStatus marks a sale row sharing purchase_transactions' idempotency
	// UNIQUE constraint (09B 3.7).
	saleStatus = "sale"
)

// economySubject is the NATS subject economy events are published to (14.3).
func economySubject(worldID string) string {
	return "world." + worldID + ".event.economy"
}

// BuyerRegistration is one RegisterBuyer request plus its pre-generated stock.
// The stock is generated deterministically from seed by the economy package
// before the tx opens — generation must not depend on DB state (09B 3.5).
type BuyerRegistration struct {
	IdempotencyKey   string
	WorldID          string
	RegionID         string
	Seed             int64
	InventoryTableID string
	PriceModifierBP  int32
	SpawnAt          time.Time
	DespawnAt        time.Time
	Stock            []StockRow
}

// StockRow is one buyer_stock row. StockEntryID is assigned by the API.
type StockRow struct {
	StockEntryID      string
	ItemDefinitionID  string
	UnitPrice         int64
	RemainingQuantity int
	Version           int64
}

// RegisterBuyer persists a Buyer and its finite stock in one transaction and
// enqueues the buyer_spawned economy event. It is idempotent on
// IdempotencyKey: a replay returns the already-persisted buyer and stock
// without regenerating (the DS may retry the RPC).
func (s *Store) RegisterBuyer(ctx context.Context, r BuyerRegistration) (string, []StockRow, error) {
	buyerID, stock, raced, err := s.registerBuyerOnce(ctx, r)
	if err != nil {
		return "", nil, err
	}
	// A concurrent registration with the same key won the INSERT. Its row is now
	// committed, so a second attempt takes the idempotent-replay path and cannot
	// race again.
	if raced {
		buyerID, stock, _, err = s.registerBuyerOnce(ctx, r)
		if err != nil {
			return "", nil, err
		}
	}
	return buyerID, stock, nil
}

// registerBuyerOnce is one RegisterBuyer attempt. raced reports that another
// transaction committed the same idempotency_key first.
func (s *Store) registerBuyerOnce(ctx context.Context, r BuyerRegistration) (buyerID string, stock []StockRow, raced bool, err error) {
	tx, err := s.pool.Begin(ctx)
	if err != nil {
		return "", nil, false, err
	}
	defer tx.Rollback(ctx) //nolint:errcheck // best-effort on non-committed tx

	// Idempotent replay: return what was already registered.
	var existingID string
	err = tx.QueryRow(ctx,
		`SELECT buyer_instance_id::text FROM buyer_instances WHERE idempotency_key = $1`,
		r.IdempotencyKey,
	).Scan(&existingID)
	if err == nil {
		existingStock, err := stockOfTx(ctx, tx, existingID)
		if err != nil {
			return "", nil, false, err
		}
		if err := tx.Commit(ctx); err != nil {
			return "", nil, false, err
		}
		return existingID, existingStock, false, nil
	}
	if !errors.Is(err, pgx.ErrNoRows) {
		return "", nil, false, fmt.Errorf("store: register buyer lookup: %w", err)
	}

	buyerID = NewUUID()
	if _, err := tx.Exec(ctx,
		`INSERT INTO buyer_instances
		   (buyer_instance_id, idempotency_key, world_id, region_id, seed,
		    inventory_table_id, price_modifier_bp, spawn_at, despawn_at, status)
		 VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)`,
		buyerID, r.IdempotencyKey, r.WorldID, r.RegionID, r.Seed,
		r.InventoryTableID, r.PriceModifierBP, r.SpawnAt, r.DespawnAt, BuyerStatusActive,
	); err != nil {
		// A racing registration with the same key committed first. Release our tx
		// (and its locks) and let the caller retry into the replay path.
		if isUniqueViolation(err) {
			_ = tx.Rollback(ctx)
			return "", nil, true, nil
		}
		return "", nil, false, fmt.Errorf("store: insert buyer: %w", err)
	}

	for i := range r.Stock {
		if r.Stock[i].StockEntryID == "" {
			r.Stock[i].StockEntryID = NewUUID()
		}
		if _, err := tx.Exec(ctx,
			`INSERT INTO buyer_stock
			   (stock_entry_id, buyer_instance_id, item_definition_id, unit_price, remaining_quantity)
			 VALUES ($1, $2, $3, $4, $5)`,
			r.Stock[i].StockEntryID, buyerID, r.Stock[i].ItemDefinitionID,
			r.Stock[i].UnitPrice, r.Stock[i].RemainingQuantity,
		); err != nil {
			return "", nil, false, fmt.Errorf("store: insert buyer stock: %w", err)
		}
	}

	payload, err := json.Marshal(map[string]any{
		"type":              "buyer_spawned",
		"buyer_instance_id": buyerID,
		"region_id":         r.RegionID,
		"stock_count":       len(r.Stock),
		"despawn_at":        r.DespawnAt.UTC().Format(time.RFC3339),
	})
	if err != nil {
		return "", nil, false, fmt.Errorf("store: marshal buyer_spawned: %w", err)
	}
	if err := enqueueOutboxTx(ctx, tx, economySubject(r.WorldID), payload); err != nil {
		return "", nil, false, err
	}

	if err := tx.Commit(ctx); err != nil {
		return "", nil, false, err
	}
	return buyerID, r.Stock, false, nil
}

// DespawnBuyer moves a Buyer to 'preparing' (reject new purchases, let started
// transactions finish) or 'despawned' (close out the remaining stock and
// announce buyer_despawned). Unknown buyers report ErrNotFound.
func (s *Store) DespawnBuyer(ctx context.Context, buyerInstanceID, targetStatus string) error {
	tx, err := s.pool.Begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback(ctx) //nolint:errcheck // best-effort on non-committed tx

	// lifecycle は active → preparing → despawned の一方向のみ。DS の再送や順序逆転で
	// 締めた Buyer が preparing に戻ると、以降も売買を受け続けてしまう。
	// 逆行/現状維持は UPDATE が 0 行になり、buyer_despawned の二重発行も防ぐ。
	var worldID string
	err = tx.QueryRow(ctx,
		`UPDATE buyer_instances SET status = $2
		  WHERE buyer_instance_id = $1 AND status <> $2 AND status <> $3
		 RETURNING world_id`,
		buyerInstanceID, targetStatus, BuyerStatusDespawned,
	).Scan(&worldID)
	if errors.Is(err, pgx.ErrNoRows) {
		// 行が無い or 遷移が不許可。存在するなら「既にその状態」＝冪等成功とする。
		var exists bool
		if err := tx.QueryRow(ctx,
			`SELECT EXISTS(SELECT 1 FROM buyer_instances WHERE buyer_instance_id = $1)`,
			buyerInstanceID,
		).Scan(&exists); err != nil {
			return fmt.Errorf("store: despawn buyer lookup: %w", err)
		}
		if !exists {
			return ErrNotFound
		}
		return tx.Commit(ctx)
	}
	if err != nil {
		return fmt.Errorf("store: despawn buyer: %w", err)
	}

	if targetStatus == BuyerStatusDespawned {
		// 締め: 残在庫を 0 にして、以降どの経路からも買えない状態にする。
		if _, err := tx.Exec(ctx,
			`UPDATE buyer_stock SET remaining_quantity = 0, version = version + 1
			  WHERE buyer_instance_id = $1 AND remaining_quantity > 0`,
			buyerInstanceID,
		); err != nil {
			return fmt.Errorf("store: close out stock: %w", err)
		}
		payload, err := json.Marshal(map[string]any{
			"type":              "buyer_despawned",
			"buyer_instance_id": buyerInstanceID,
		})
		if err != nil {
			return fmt.Errorf("store: marshal buyer_despawned: %w", err)
		}
		if err := enqueueOutboxTx(ctx, tx, economySubject(worldID), payload); err != nil {
			return err
		}
	}
	return tx.Commit(ctx)
}

// PurchaseInput is a CommitPurchase request (09B 3.6).
type PurchaseInput struct {
	IdempotencyKey   string
	BuyerInstanceID  string
	StockEntryID     string
	PurchaserID      string
	InventoryVersion int64
}

// PurchaseResult is the outcome returned to the DS and replayed verbatim for a
// duplicate idempotency_key (AT-019).
type PurchaseResult struct {
	Outcome              PurchaseOutcome
	GrantedDefinitionIDs []string
	ItemInstanceIDs      []string
	NewInventoryVersion  int64
	Charged              int64
}

// CommitPurchase confirms a purchase in a single transaction (MVP 12.2):
// idempotency check → stock row lock → verification → stock decrement → ledger →
// item instance → inventory → transaction record → outbox. Any rejection rolls
// back with no stock/ledger movement.
func (s *Store) CommitPurchase(ctx context.Context, in PurchaseInput) (PurchaseResult, error) {
	tx, err := s.pool.Begin(ctx)
	if err != nil {
		return PurchaseResult{}, err
	}
	defer tx.Rollback(ctx) //nolint:errcheck // best-effort on non-committed tx

	// Serialize this purchaser's account for the whole tx before reading their
	// balance, so two concurrent purchases can never spend the same funds twice
	// (MVP-SEC-009 口座の直列化). Taken before the stock lock and keyed only by
	// purchaser, so it cannot deadlock against another purchaser's lock.
	if _, err := tx.Exec(ctx, `SELECT pg_advisory_xact_lock(hashtext($1))`, in.PurchaserID); err != nil {
		return PurchaseResult{}, fmt.Errorf("store: purchaser lock: %w", err)
	}

	// 1) 冪等チェック: 確定済みなら保存済み結果を復元して返す（何も動かさない）。
	if res, ok, err := replayPurchaseTx(ctx, tx, in.IdempotencyKey); err != nil {
		return PurchaseResult{}, err
	} else if ok {
		return res, nil
	}

	// 2) 在庫行ロック（同時購入で 1 件のみ成功させる）。
	var (
		remaining   int
		unitPrice   int64
		itemDefID   string
		stockVer    int64
		buyerStatus string
		worldID     string
	)
	// stock_entry_id と buyer_instance_id の組で引く。組で照合しないと、別 Buyer の
	// 在庫を減らしつつ台帳/イベントには要求された buyer_instance_id を書いてしまう。
	err = tx.QueryRow(ctx,
		`SELECT bs.remaining_quantity, bs.unit_price, bs.item_definition_id, bs.version,
		        bi.status, bi.world_id
		   FROM buyer_stock bs
		   JOIN buyer_instances bi ON bi.buyer_instance_id = bs.buyer_instance_id
		  WHERE bs.stock_entry_id = $1 AND bs.buyer_instance_id = $2
		    FOR UPDATE OF bs`,
		in.StockEntryID, in.BuyerInstanceID,
	).Scan(&remaining, &unitPrice, &itemDefID, &stockVer, &buyerStatus, &worldID)
	if errors.Is(err, pgx.ErrNoRows) {
		return PurchaseResult{Outcome: PurchaseRejected}, nil
	}
	if err != nil {
		return PurchaseResult{}, fmt.Errorf("store: lock stock: %w", err)
	}

	// 3) 検証。
	if buyerStatus != BuyerStatusActive {
		return PurchaseResult{Outcome: PurchaseRejected}, nil
	}
	if remaining <= 0 {
		return PurchaseResult{Outcome: PurchaseOutOfStock}, nil
	}

	invID, err := ensureInventoryTx(ctx, tx, in.PurchaserID)
	if err != nil {
		return PurchaseResult{}, err
	}
	var invVersion int64
	if err := tx.QueryRow(ctx,
		`SELECT version FROM inventories WHERE inventory_id = $1`, invID,
	).Scan(&invVersion); err != nil {
		return PurchaseResult{}, fmt.Errorf("store: inventory version: %w", err)
	}
	if in.InventoryVersion != invVersion {
		return PurchaseResult{Outcome: PurchaseRejected}, nil
	}

	def, err := itemDefEffectTx(ctx, tx, itemDefID)
	if err != nil {
		return PurchaseResult{}, err
	}
	// 満杯インベントリは在庫・台帳を動かさず REJECTED（AT-004 相当）。
	hasRoom, err := hasRoomTx(ctx, tx, invID, itemDefID, def)
	if err != nil {
		return PurchaseResult{}, err
	}
	if !hasRoom {
		return PurchaseResult{Outcome: PurchaseRejected}, nil
	}

	balance, err := balanceTx(ctx, tx, in.PurchaserID)
	if err != nil {
		return PurchaseResult{}, err
	}
	if balance-unitPrice < 0 {
		return PurchaseResult{Outcome: PurchaseInsufficientFunds}, nil
	}

	// 4) 在庫減算（version 条件更新）。0 行なら他者が先に確定した競合。
	tag, err := tx.Exec(ctx,
		`UPDATE buyer_stock SET remaining_quantity = remaining_quantity - 1, version = version + 1
		  WHERE stock_entry_id = $1 AND version = $2 AND remaining_quantity > 0`,
		in.StockEntryID, stockVer,
	)
	if err != nil {
		return PurchaseResult{}, fmt.Errorf("store: decrement stock: %w", err)
	}
	if tag.RowsAffected() == 0 {
		return PurchaseResult{Outcome: PurchaseOutOfStock}, nil
	}

	// 5) 通貨台帳（唯一の Writer 経由。口座ロックは冒頭で取得済み）。
	purchaseID := NewUUID()
	if _, err := appendCurrencyTx(ctx, tx, in.PurchaserID, -unitPrice, "purchase", &purchaseID); err != nil {
		return PurchaseResult{}, err
	}

	// 6/7) Item 個体生成 + インベントリ確定（永続 Writer=API）。
	// stackable では個体を作らないが、nil スライスは TEXT[] へ NULL として入り
	// item_instance_ids の NOT NULL に違反する（apply.go の world_items.tags と同じ罠）。
	instanceIDs := []string{}
	if def.IsInstance {
		instanceID := NewUUID()
		if err := addInstanceTx(ctx, tx, invID, itemStack{
			ItemDefinitionID: itemDefID,
			ItemInstanceID:   instanceID,
			Quantity:         1,
		}); err != nil {
			return PurchaseResult{}, err
		}
		instanceIDs = append(instanceIDs, instanceID)
	} else if err := addStackTx(ctx, tx, invID, itemDefID, 1, def.StackLimit); err != nil {
		return PurchaseResult{}, err
	}
	if err := bumpInventoryVersionTx(ctx, tx, invID); err != nil {
		return PurchaseResult{}, err
	}
	newInvVersion := invVersion + 1

	// 8) 取引記録。UNIQUE 違反＝並行同一キー: 既存結果を読み直して同じ結果を返す
	//    （二重に在庫/台帳を動かさない・09B 6章）。
	grantedDefs := []string{itemDefID}
	sp, err := tx.Begin(ctx) // nested savepoint
	if err != nil {
		return PurchaseResult{}, err
	}
	_, err = sp.Exec(ctx,
		`INSERT INTO purchase_transactions
		   (purchase_id, idempotency_key, buyer, purchaser, amount, status,
		    stock_entry_id, item_instance_ids, granted_definition_ids, new_inventory_version)
		 VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)`,
		purchaseID, in.IdempotencyKey, in.BuyerInstanceID, in.PurchaserID, unitPrice,
		string(PurchaseCommitted), in.StockEntryID, instanceIDs, grantedDefs, newInvVersion,
	)
	if err != nil {
		_ = sp.Rollback(ctx)
		if isUniqueViolation(err) {
			// 先行 Tx が同じキーで確定済み。こちらの在庫減算/台帳を捨ててから
			// （＝ロックを解放してから）既存結果を読み直す（09B 6章 冪等の穴）。
			_ = tx.Rollback(ctx)
			return s.replayCommittedPurchase(ctx, in.IdempotencyKey)
		}
		return PurchaseResult{}, fmt.Errorf("store: insert purchase: %w", err)
	}
	if err := sp.Commit(ctx); err != nil {
		return PurchaseResult{}, err
	}

	// 9) 同一 Tx で Outbox へ（発行は relay が非同期に行う・ゲームを止めない）。
	payload, err := json.Marshal(map[string]any{
		"type":               "purchase",
		"purchase_id":        purchaseID,
		"buyer_instance_id":  in.BuyerInstanceID,
		"stock_entry_id":     in.StockEntryID,
		"purchaser":          in.PurchaserID,
		"item_definition_id": itemDefID,
		"amount":             unitPrice,
		"remaining_quantity": remaining - 1,
	})
	if err != nil {
		return PurchaseResult{}, fmt.Errorf("store: marshal purchase event: %w", err)
	}
	if err := enqueueOutboxTx(ctx, tx, economySubject(worldID), payload); err != nil {
		return PurchaseResult{}, err
	}

	if err := tx.Commit(ctx); err != nil {
		return PurchaseResult{}, err
	}
	return PurchaseResult{
		Outcome:              PurchaseCommitted,
		GrantedDefinitionIDs: grantedDefs,
		ItemInstanceIDs:      instanceIDs,
		NewInventoryVersion:  newInvVersion,
		Charged:              unitPrice,
	}, nil
}

// replayPurchaseTx returns the stored result for key if the transaction already
// committed, restoring the original PurchaseResult (12.2 step 1 / AT-019).
func replayPurchaseTx(ctx context.Context, tx pgx.Tx, key string) (PurchaseResult, bool, error) {
	var (
		amount      int64
		instanceIDs []string
		grantedDefs []string
		invVersion  int64
		st          string
	)
	// 売却は同じ purchase_transactions / 同じ idempotency_key UNIQUE を共有するので、
	// status で購入行に限定する。限定しないと、鍵が衝突した売却行を購入のリプレイとして
	// 返し、在庫を動かさないまま「購入済み」と応答してしまう。
	err := tx.QueryRow(ctx,
		`SELECT amount, item_instance_ids, granted_definition_ids, new_inventory_version, status
		   FROM purchase_transactions WHERE idempotency_key = $1 AND status <> $2`,
		key, saleStatus,
	).Scan(&amount, &instanceIDs, &grantedDefs, &invVersion, &st)
	if errors.Is(err, pgx.ErrNoRows) {
		return PurchaseResult{}, false, nil
	}
	if err != nil {
		return PurchaseResult{}, false, fmt.Errorf("store: purchase replay: %w", err)
	}
	return PurchaseResult{
		Outcome:              PurchaseDuplicate,
		GrantedDefinitionIDs: grantedDefs,
		ItemInstanceIDs:      instanceIDs,
		NewInventoryVersion:  invVersion,
		Charged:              amount,
	}, true, nil
}

// replayCommittedPurchase re-reads a concurrently committed purchase in a fresh
// transaction, after the caller's has been rolled back.
func (s *Store) replayCommittedPurchase(ctx context.Context, key string) (PurchaseResult, error) {
	tx, err := s.pool.Begin(ctx)
	if err != nil {
		return PurchaseResult{}, err
	}
	defer tx.Rollback(ctx) //nolint:errcheck // read-only
	res, ok, err := replayPurchaseTx(ctx, tx, key)
	if err != nil {
		return PurchaseResult{}, err
	}
	if !ok {
		// 先行 Tx が UNIQUE 違反を起こしたのに行が無い＝そちらがロールバック済み。
		// 在庫/台帳も戻っているので競合として返す（DS は再送してよい）。
		return PurchaseResult{Outcome: PurchaseRejected}, nil
	}
	return res, nil
}

// SaleInput is a CommitSale request (09B 3.7).
type SaleInput struct {
	IdempotencyKey  string
	BuyerInstanceID string
	SellerID        string
	Items           []SaleItem
}

// SaleItem is one item being sold. UnitSellPrice is resolved by the API from the
// Item Definition master — a Client-supplied price is never used (MVP-SEC-006).
type SaleItem struct {
	ItemDefinitionID string
	ItemInstanceID   string
	UnitSellPrice    int64
}

// SaleOutcome is the result of CommitSale.
type SaleOutcome string

const (
	// SaleOK means the sale committed.
	SaleOK SaleOutcome = "ok"
	// SaleDuplicate means the idempotency_key replayed.
	SaleDuplicate SaleOutcome = "duplicate"
	// SaleRejected means the seller does not hold the items (or none were given).
	SaleRejected SaleOutcome = "rejected"
)

// SaleResult is what the DS gets back.
type SaleResult struct {
	Outcome             SaleOutcome
	Proceeds            int64
	NewInventoryVersion int64
}

// CommitSale confirms a sale in a single transaction: verify ownership, remove
// from inventory, credit the ledger, bump the version, record for idempotency
// and enqueue the sale event. Sales share purchase_transactions' idempotency_key
// UNIQUE via status='sale' (09B 3.7).
func (s *Store) CommitSale(ctx context.Context, in SaleInput) (SaleResult, error) {
	if len(in.Items) == 0 {
		return SaleResult{Outcome: SaleRejected}, nil
	}

	tx, err := s.pool.Begin(ctx)
	if err != nil {
		return SaleResult{}, err
	}
	defer tx.Rollback(ctx) //nolint:errcheck // best-effort on non-committed tx

	// 購入と同じく口座を直列化してから残高を読む（MVP-SEC-009）。
	if _, err := tx.Exec(ctx, `SELECT pg_advisory_xact_lock(hashtext($1))`, in.SellerID); err != nil {
		return SaleResult{}, fmt.Errorf("store: seller lock: %w", err)
	}

	// 冪等チェック（購入行と鍵空間を共有するため status='sale' に限定する）。
	if res, ok, err := replaySaleTx(ctx, tx, in.IdempotencyKey); err != nil {
		return SaleResult{}, err
	} else if ok {
		return res, nil
	}

	var worldID, buyerStatus string
	if err := tx.QueryRow(ctx,
		`SELECT world_id, status FROM buyer_instances WHERE buyer_instance_id = $1`, in.BuyerInstanceID,
	).Scan(&worldID, &buyerStatus); err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return SaleResult{Outcome: SaleRejected}, nil
		}
		return SaleResult{}, fmt.Errorf("store: sale buyer lookup: %w", err)
	}
	// 購入と同じ lifecycle を売却にも課す: preparing 以降の Buyer は新規取引を受けない。
	// これが無いと、DS 上から消えた Buyer に売り続けられる（09B 3.3）。
	if buyerStatus != BuyerStatusActive {
		return SaleResult{Outcome: SaleRejected}, nil
	}

	invID, err := ensureInventoryTx(ctx, tx, in.SellerID)
	if err != nil {
		return SaleResult{}, err
	}
	var invVersion int64
	if err := tx.QueryRow(ctx,
		`SELECT version FROM inventories WHERE inventory_id = $1`, invID,
	).Scan(&invVersion); err != nil {
		return SaleResult{}, fmt.Errorf("store: inventory version: %w", err)
	}

	var proceeds int64
	defIDs := make([]string, 0, len(in.Items))
	for _, it := range in.Items {
		held, err := holdsItemTx(ctx, tx, invID, it)
		if err != nil {
			return SaleResult{}, err
		}
		// 所有していないものは売れない。在庫・台帳を動かさず REJECTED（3.7）。
		if !held {
			return SaleResult{Outcome: SaleRejected}, nil
		}
		if it.ItemInstanceID != "" {
			if err := removeInstanceTx(ctx, tx, invID, it.ItemInstanceID); err != nil {
				return SaleResult{}, err
			}
			// 個体は売却で消費する（世界から取り除く）。
			if _, err := tx.Exec(ctx,
				`DELETE FROM item_instances WHERE item_instance_id = $1`, it.ItemInstanceID,
			); err != nil {
				return SaleResult{}, fmt.Errorf("store: delete sold instance: %w", err)
			}
		} else if err := removeStackTx(ctx, tx, invID, it.ItemDefinitionID, 1); err != nil {
			return SaleResult{}, err
		}
		proceeds += it.UnitSellPrice // 整数合算（float 禁止・13.1）
		defIDs = append(defIDs, it.ItemDefinitionID)
	}

	saleID := NewUUID()
	if _, err := appendCurrencyTx(ctx, tx, in.SellerID, proceeds, "sale", &saleID); err != nil {
		return SaleResult{}, err
	}
	if err := bumpInventoryVersionTx(ctx, tx, invID); err != nil {
		return SaleResult{}, err
	}
	newInvVersion := invVersion + 1

	if _, err := tx.Exec(ctx,
		`INSERT INTO purchase_transactions
		   (purchase_id, idempotency_key, buyer, purchaser, amount, status,
		    granted_definition_ids, new_inventory_version)
		 VALUES ($1, $2, $3, $4, $5, $6, $7, $8)`,
		saleID, in.IdempotencyKey, in.BuyerInstanceID, in.SellerID, proceeds,
		saleStatus, defIDs, newInvVersion,
	); err != nil {
		if isUniqueViolation(err) {
			// 並行同一キー: 先行が確定済み。こちらの変更を捨ててロックを解放してから、
			// 保存済みの結果を読み直して返す。0 を返すと DS が inventory version を
			// 0 で上書きし、以後の version 条件購入が全て REJECTED になる。
			_ = tx.Rollback(ctx)
			return s.replayCommittedSale(ctx, in.IdempotencyKey)
		}
		return SaleResult{}, fmt.Errorf("store: insert sale: %w", err)
	}

	payload, err := json.Marshal(map[string]any{
		"type":                "sale",
		"seller":              in.SellerID,
		"buyer_instance_id":   in.BuyerInstanceID,
		"proceeds":            proceeds,
		"item_definition_ids": defIDs,
	})
	if err != nil {
		return SaleResult{}, fmt.Errorf("store: marshal sale event: %w", err)
	}
	if err := enqueueOutboxTx(ctx, tx, economySubject(worldID), payload); err != nil {
		return SaleResult{}, err
	}

	if err := tx.Commit(ctx); err != nil {
		return SaleResult{}, err
	}
	return SaleResult{Outcome: SaleOK, Proceeds: proceeds, NewInventoryVersion: newInvVersion}, nil
}

// replaySaleTx returns the stored result for a already-committed sale with key.
// It matches only status='sale' rows, since purchases share the same
// idempotency_key UNIQUE constraint (09B 3.7).
func replaySaleTx(ctx context.Context, tx pgx.Tx, key string) (SaleResult, bool, error) {
	var amount, invVersion int64
	err := tx.QueryRow(ctx,
		`SELECT amount, new_inventory_version FROM purchase_transactions
		  WHERE idempotency_key = $1 AND status = $2`,
		key, saleStatus,
	).Scan(&amount, &invVersion)
	if errors.Is(err, pgx.ErrNoRows) {
		return SaleResult{}, false, nil
	}
	if err != nil {
		return SaleResult{}, false, fmt.Errorf("store: sale replay: %w", err)
	}
	return SaleResult{
		Outcome:             SaleDuplicate,
		Proceeds:            amount,
		NewInventoryVersion: invVersion,
	}, true, nil
}

// replayCommittedSale re-reads a concurrently committed sale in a fresh
// transaction, after the caller's has been rolled back.
func (s *Store) replayCommittedSale(ctx context.Context, key string) (SaleResult, error) {
	tx, err := s.pool.Begin(ctx)
	if err != nil {
		return SaleResult{}, err
	}
	defer tx.Rollback(ctx) //nolint:errcheck // read-only
	res, ok, err := replaySaleTx(ctx, tx, key)
	if err != nil {
		return SaleResult{}, err
	}
	if !ok {
		// 鍵は取られているが sale 行が無い＝購入が同じ鍵を使っている（または先行が
		// ロールバック済み）。二重確定を避けて拒否する。
		return SaleResult{Outcome: SaleRejected}, nil
	}
	return res, nil
}

// holdsItemTx reports whether the inventory actually holds the item being sold.
//
// The instance branch must also pin item_definition_id: the sale is priced from
// the caller-supplied definition, so accepting an instance whose real definition
// differs would let a seller hand over a cheap item and be paid for an expensive
// one — minting currency (MVP-SEC-006). The definition is checked against
// item_instances (the authoritative individual), not the entry alone.
func holdsItemTx(ctx context.Context, tx pgx.Tx, invID string, it SaleItem) (bool, error) {
	var held bool
	var err error
	if it.ItemInstanceID != "" {
		err = tx.QueryRow(ctx,
			`SELECT EXISTS(SELECT 1 FROM inventory_entries e
			   JOIN item_instances i ON i.item_instance_id = e.item_instance_id
			  WHERE e.inventory_id = $1 AND e.item_instance_id = $2
			    AND i.definition_id = $3)`,
			invID, it.ItemInstanceID, it.ItemDefinitionID,
		).Scan(&held)
	} else {
		err = tx.QueryRow(ctx,
			`SELECT coalesce(sum(quantity), 0) >= 1 FROM inventory_entries
			  WHERE inventory_id = $1 AND item_definition_id = $2 AND item_instance_id IS NULL`,
			invID, it.ItemDefinitionID,
		).Scan(&held)
	}
	if err != nil {
		return false, fmt.Errorf("store: check holding: %w", err)
	}
	return held, nil
}

// balanceTx returns the owner's current balance: the latest ledger entry's
// balance_after, or 0 when they have no entries. The caller must already hold
// the purchaser's advisory lock (MVP-SEC-009).
func balanceTx(ctx context.Context, tx pgx.Tx, ownerID string) (int64, error) {
	var balance int64
	if err := tx.QueryRow(ctx,
		`SELECT coalesce((
		    SELECT balance_after FROM currency_ledger
		     WHERE owner_id = $1 ORDER BY created_at DESC, entry_id DESC LIMIT 1), 0)`,
		ownerID,
	).Scan(&balance); err != nil {
		return 0, fmt.Errorf("store: balance: %w", err)
	}
	return balance, nil
}

// hasRoomTx reports whether one more unit of itemDefID fits: an individual needs
// a free slot, a stackable can also top up an existing partial stack (AT-004).
func hasRoomTx(ctx context.Context, tx pgx.Tx, invID, itemDefID string, def itemDefEffect) (bool, error) {
	if !def.IsInstance {
		var partial bool
		if err := tx.QueryRow(ctx,
			`SELECT EXISTS(SELECT 1 FROM inventory_entries
			   WHERE inventory_id = $1 AND item_definition_id = $2
			     AND item_instance_id IS NULL AND quantity < $3)`,
			invID, itemDefID, def.StackLimit,
		).Scan(&partial); err != nil {
			return false, fmt.Errorf("store: check partial stack: %w", err)
		}
		if partial {
			return true, nil
		}
	}
	var used, capacity int
	if err := tx.QueryRow(ctx,
		`SELECT (SELECT count(*) FROM inventory_entries WHERE inventory_id = $1),
		        (SELECT slot_capacity FROM inventories WHERE inventory_id = $1)`,
		invID,
	).Scan(&used, &capacity); err != nil {
		return false, fmt.Errorf("store: check capacity: %w", err)
	}
	return used < capacity, nil
}

// stockOfTx returns a buyer's stock rows, ordered for a stable response.
func stockOfTx(ctx context.Context, tx pgx.Tx, buyerInstanceID string) ([]StockRow, error) {
	rows, err := tx.Query(ctx,
		`SELECT stock_entry_id::text, item_definition_id, unit_price, remaining_quantity, version
		   FROM buyer_stock WHERE buyer_instance_id = $1 ORDER BY stock_entry_id ASC`,
		buyerInstanceID,
	)
	if err != nil {
		return nil, fmt.Errorf("store: read stock: %w", err)
	}
	defer rows.Close()

	var out []StockRow
	for rows.Next() {
		var r StockRow
		if err := rows.Scan(&r.StockEntryID, &r.ItemDefinitionID, &r.UnitPrice,
			&r.RemainingQuantity, &r.Version); err != nil {
			return nil, fmt.Errorf("store: scan stock: %w", err)
		}
		out = append(out, r)
	}
	return out, rows.Err()
}

// enqueueOutboxTx inserts an outbox row inside an existing transaction, so the
// NATS publish happens only after the economy change durably commits and never
// blocks the tick (09B 3.8 / MVP 16).
func enqueueOutboxTx(ctx context.Context, tx pgx.Tx, topic string, payload []byte) error {
	if _, err := tx.Exec(ctx,
		`INSERT INTO outbox_messages (message_id, topic, payload) VALUES ($1, $2, $3::jsonb)`,
		NewUUID(), topic, jsonbArg(payload),
	); err != nil {
		return fmt.Errorf("store: enqueue outbox: %w", err)
	}
	return nil
}

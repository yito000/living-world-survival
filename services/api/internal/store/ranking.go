package store

import (
	"context"
	"fmt"
)

// This file backs the M6 asset ranking batch (MVP 12.3 / 09B 3.9):
// net_worth = 現金 + Item評価額 + 設備評価額, all integers (13.1).

// OwnerNetWorth is one owner's computed net worth for a ranking run.
type OwnerNetWorth struct {
	OwnerID  string
	NetWorth int64
}

// ComputeNetWorth returns each owner's net worth: their latest ledger balance plus
// the sell_price valuation of everything they hold. Owners are every party with
// an inventory or a ledger account (Player and AI alike — the API does not
// distinguish them, AT-013).
//
// 設備評価額 is 0 in the MVP: there is no per-owner facility table yet
// (world_blueprints is world-scoped, not owned), so no facility contributes to
// any owner. Definitions with no price count as 0 and do not affect the ranking
// (09B 2章 注). The term is summed explicitly below so adding an owned-facility
// table later is a one-query change.
func (s *Store) ComputeNetWorth(ctx context.Context) ([]OwnerNetWorth, error) {
	rows, err := s.pool.Query(ctx,
		`WITH owners AS (
		     SELECT owner_id FROM inventories
		     UNION
		     SELECT owner_id FROM currency_ledger
		 ),
		 cash AS (
		     SELECT DISTINCT ON (owner_id) owner_id, balance_after
		       FROM currency_ledger
		      ORDER BY owner_id, created_at DESC, entry_id DESC
		 ),
		 items AS (
		     SELECT i.owner_id,
		            coalesce(sum(e.quantity::bigint * d.sell_price), 0) AS value
		       FROM inventories i
		       JOIN inventory_entries e ON e.inventory_id = i.inventory_id
		       LEFT JOIN item_definitions d ON d.item_definition_id = e.item_definition_id
		      GROUP BY i.owner_id
		 )
		 SELECT o.owner_id,
		        coalesce(c.balance_after, 0) + coalesce(it.value, 0) + 0 AS net_worth
		   FROM owners o
		   LEFT JOIN cash  c  ON c.owner_id = o.owner_id
		   LEFT JOIN items it ON it.owner_id = o.owner_id
		  ORDER BY net_worth DESC, o.owner_id ASC`,
	)
	if err != nil {
		return nil, fmt.Errorf("store: compute net worth: %w", err)
	}
	defer rows.Close()

	var out []OwnerNetWorth
	for rows.Next() {
		var n OwnerNetWorth
		if err := rows.Scan(&n.OwnerID, &n.NetWorth); err != nil {
			return nil, fmt.Errorf("store: scan net worth: %w", err)
		}
		out = append(out, n)
	}
	return out, rows.Err()
}

// SaveRanking writes one ranking run: every row shares priceVersion and
// calculated_at defaults to now(). Past runs are kept as generations (12.3).
func (s *Store) SaveRanking(ctx context.Context, priceVersion int64, entries []OwnerNetWorth) error {
	if len(entries) == 0 {
		return nil
	}
	tx, err := s.pool.Begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback(ctx) //nolint:errcheck // best-effort on non-committed tx

	for _, e := range entries {
		if _, err := tx.Exec(ctx,
			`INSERT INTO asset_rankings (rank_id, owner_id, net_worth, price_version)
			 VALUES ($1, $2, $3, $4)`,
			NewUUID(), e.OwnerID, e.NetWorth, priceVersion,
		); err != nil {
			return fmt.Errorf("store: insert ranking: %w", err)
		}
	}
	return tx.Commit(ctx)
}

// NextPriceVersion returns the next monotonically increasing run version — the
// version of the price table the run valued items at (12.3).
func (s *Store) NextPriceVersion(ctx context.Context) (int64, error) {
	var v int64
	if err := s.pool.QueryRow(ctx,
		`SELECT coalesce(max(price_version), 0) + 1 FROM asset_rankings`,
	).Scan(&v); err != nil {
		return 0, fmt.Errorf("store: next price version: %w", err)
	}
	return v, nil
}

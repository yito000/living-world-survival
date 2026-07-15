package store

import (
	"context"
	"fmt"
)

// UpsertItemDefinition seeds/updates one Item Definition master row (3.8). It is
// idempotent (ON CONFLICT DO UPDATE). useEffect must be valid JSON bytes.
func (s *Store) UpsertItemDefinition(ctx context.Context, id string, tags []string, stackLimit, weightMilli, rarity int, baseValue int64, useEffect []byte) error {
	_, err := s.pool.Exec(ctx,
		`INSERT INTO item_definitions
		   (item_definition_id, tags, stack_limit, weight_milli, rarity, base_value, use_effect)
		 VALUES ($1, $2, $3, $4, $5, $6, $7::jsonb)
		 ON CONFLICT (item_definition_id) DO UPDATE
		    SET tags = EXCLUDED.tags,
		        stack_limit = EXCLUDED.stack_limit,
		        weight_milli = EXCLUDED.weight_milli,
		        rarity = EXCLUDED.rarity,
		        base_value = EXCLUDED.base_value,
		        use_effect = EXCLUDED.use_effect`,
		id, tags, stackLimit, weightMilli, rarity, baseValue, jsonbArg(useEffect),
	)
	if err != nil {
		return fmt.Errorf("store: upsert item definition %s: %w", id, err)
	}
	return nil
}

// CountItemDefinitions returns how many definitions are currently seeded.
func (s *Store) CountItemDefinitions(ctx context.Context) (int, error) {
	var n int
	if err := s.pool.QueryRow(ctx, `SELECT count(*) FROM item_definitions`).Scan(&n); err != nil {
		return 0, fmt.Errorf("store: count item definitions: %w", err)
	}
	return n, nil
}

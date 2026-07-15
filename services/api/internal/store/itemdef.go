package store

import (
	"context"
	"fmt"
)

// ItemDefinitionRow is one Item Definition master row (7.2 / 06B 3.1). Weight is
// a milli-integer and base_value a BIGINT — no floats for weight/currency (13.1).
type ItemDefinitionRow struct {
	ID            string
	PrimaryTag    string
	Tags          []string
	StackLimit    int
	WeightMilli   int
	Rarity        int
	BaseValue     int64
	ConsumeHunger int
	WasteOutput   int
	IsInstance    bool
	UseEffect     []byte
}

// UpsertItemDefinition seeds/updates one Item Definition master row (3.8). It is
// idempotent (ON CONFLICT DO UPDATE). UseEffect must be valid JSON bytes.
func (s *Store) UpsertItemDefinition(ctx context.Context, d ItemDefinitionRow) error {
	_, err := s.pool.Exec(ctx,
		`INSERT INTO item_definitions
		   (item_definition_id, primary_tag, tags, stack_limit, weight_milli, rarity,
		    base_value, consume_hunger, waste_output, is_instance, use_effect)
		 VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11::jsonb)
		 ON CONFLICT (item_definition_id) DO UPDATE
		    SET primary_tag    = EXCLUDED.primary_tag,
		        tags           = EXCLUDED.tags,
		        stack_limit    = EXCLUDED.stack_limit,
		        weight_milli   = EXCLUDED.weight_milli,
		        rarity         = EXCLUDED.rarity,
		        base_value     = EXCLUDED.base_value,
		        consume_hunger = EXCLUDED.consume_hunger,
		        waste_output   = EXCLUDED.waste_output,
		        is_instance    = EXCLUDED.is_instance,
		        use_effect     = EXCLUDED.use_effect`,
		d.ID, d.PrimaryTag, d.Tags, d.StackLimit, d.WeightMilli, d.Rarity,
		d.BaseValue, d.ConsumeHunger, d.WasteOutput, d.IsInstance, jsonbArg(d.UseEffect),
	)
	if err != nil {
		return fmt.Errorf("store: upsert item definition %s: %w", d.ID, err)
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

// itemDefEffect caches the seeded is_instance / stack_limit for an item, read
// inside the AppendEvents tx to decide stack-vs-instance placement (3.2 step 3).
type itemDefEffect struct {
	StackLimit int
	IsInstance bool
}

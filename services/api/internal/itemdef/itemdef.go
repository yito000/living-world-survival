// Package itemdef loads and validates the Item Definition master (MVP 7.2) and
// seeds it into item_definitions. The master is the authoritative source both
// the API (validation) and the Unity client (SO import) use, so A/B never
// diverge (3.8). Weights are milli-integers; no floats (落とし穴6.3).
package itemdef

import (
	"context"
	"encoding/json"
	"fmt"
	"os"

	"living-world-survival/services/api/internal/store"
)

// maxRarity is the highest valid rarity (0=common..3=epic, 基本設計 6.1).
const maxRarity = 3

// Definition is one Item Definition master entry. The M3 fields (PrimaryTag,
// ConsumeHunger, WasteOutput, IsInstance — 7.2 / 06B 3.1) drive the survival
// loop: Hunger recovery on consume, waste produced by cooking, and whether the
// item is a durability/quality-bearing individual (item_instances).
//
// The M6 price fields (09B 3.4/3.7/3.9) are the authoritative economy inputs:
// BaseValue is the purchase base a Buyer's unit_price is derived from, and
// SellPrice is what CommitSale pays out and what the ranking batch values held
// items at. Both are minimum-currency-unit integers — never floats (13.1).
type Definition struct {
	ItemDefinitionID string          `json:"item_definition_id"`
	PrimaryTag       string          `json:"primary_tag"`
	Tags             []string        `json:"tags"`
	StackLimit       int             `json:"stack_limit"`
	WeightMilli      int             `json:"weight_milli"`
	Rarity           int             `json:"rarity"`
	BaseValue        int64           `json:"base_value"`
	SellPrice        int64           `json:"sell_price"`
	ConsumeHunger    int             `json:"consume_hunger"`
	WasteOutput      int             `json:"waste_output"`
	IsInstance       bool            `json:"is_instance"`
	UseEffect        json.RawMessage `json:"use_effect"`
}

type file struct {
	Definitions []Definition `json:"definitions"`
}

// Catalog is the validated set of definitions, indexed by id.
type Catalog struct {
	list []Definition
	byID map[string]Definition
}

// Load reads and validates the master JSON at path.
func Load(path string) (*Catalog, error) {
	raw, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("itemdef: read %s: %w", path, err)
	}
	var f file
	if err := json.Unmarshal(raw, &f); err != nil {
		return nil, fmt.Errorf("itemdef: parse %s: %w", path, err)
	}
	return newCatalog(f.Definitions)
}

func newCatalog(defs []Definition) (*Catalog, error) {
	if len(defs) == 0 {
		return nil, fmt.Errorf("itemdef: no definitions")
	}
	byID := make(map[string]Definition, len(defs))
	for i, d := range defs {
		if d.ItemDefinitionID == "" {
			return nil, fmt.Errorf("itemdef: definition[%d] has empty id", i)
		}
		if _, dup := byID[d.ItemDefinitionID]; dup {
			return nil, fmt.Errorf("itemdef: duplicate id %q", d.ItemDefinitionID)
		}
		if d.StackLimit <= 0 {
			return nil, fmt.Errorf("itemdef: %s stack_limit must be > 0", d.ItemDefinitionID)
		}
		if d.WeightMilli < 0 {
			return nil, fmt.Errorf("itemdef: %s weight_milli must be >= 0", d.ItemDefinitionID)
		}
		if d.Rarity < 0 || d.Rarity > maxRarity {
			return nil, fmt.Errorf("itemdef: %s rarity %d out of range 0..%d", d.ItemDefinitionID, d.Rarity, maxRarity)
		}
		if len(d.UseEffect) > 0 && !json.Valid(d.UseEffect) {
			return nil, fmt.Errorf("itemdef: %s use_effect is not valid JSON", d.ItemDefinitionID)
		}
		if d.ConsumeHunger < 0 {
			return nil, fmt.Errorf("itemdef: %s consume_hunger must be >= 0", d.ItemDefinitionID)
		}
		if d.WasteOutput < 0 {
			return nil, fmt.Errorf("itemdef: %s waste_output must be >= 0", d.ItemDefinitionID)
		}
		// Negative prices would let a purchase credit the buyer or a sale drain
		// the seller's ledger (MVP 12.2 の残高検証を素通りする).
		if d.BaseValue < 0 {
			return nil, fmt.Errorf("itemdef: %s base_value must be >= 0", d.ItemDefinitionID)
		}
		if d.SellPrice < 0 {
			return nil, fmt.Errorf("itemdef: %s sell_price must be >= 0", d.ItemDefinitionID)
		}
		// primary_tag defaults to the first tag so older masters remain valid.
		if d.PrimaryTag == "" && len(d.Tags) > 0 {
			d.PrimaryTag = d.Tags[0]
		}
		defs[i] = d
		byID[d.ItemDefinitionID] = d
	}
	return &Catalog{list: defs, byID: byID}, nil
}

// Get returns the definition for id and whether it exists. Used by validation
// paths that must reject Client-supplied quantity/quality/durability (MVP 7.3).
func (c *Catalog) Get(id string) (Definition, bool) {
	d, ok := c.byID[id]
	return d, ok
}

// All returns all definitions.
func (c *Catalog) All() []Definition { return c.list }

// ByTag returns the ids of every definition carrying tag, used by M6 Buyer stock
// generation to resolve a drawn item_tag to a concrete item (09B 3.5 手順4).
// Matching is exact against the tags list, which in the master always includes
// primary_tag.
func (c *Catalog) ByTag(tag string) []string {
	var ids []string
	for _, d := range c.list {
		for _, t := range d.Tags {
			if t == tag {
				ids = append(ids, d.ItemDefinitionID)
				break
			}
		}
	}
	return ids
}

// BaseValue returns the purchase base price for id (09B 3.4).
func (c *Catalog) BaseValue(id string) (int64, bool) {
	d, ok := c.byID[id]
	return d.BaseValue, ok
}

// SellPrice returns the sale payout / valuation price for id (09B 3.7 / 3.9).
func (c *Catalog) SellPrice(id string) (int64, bool) {
	d, ok := c.byID[id]
	return d.SellPrice, ok
}

// Len returns the number of definitions.
func (c *Catalog) Len() int { return len(c.list) }

// Seed upserts every definition into item_definitions (idempotent, 3.8).
func (c *Catalog) Seed(ctx context.Context, st *store.Store) error {
	for _, d := range c.list {
		useEffect := []byte(d.UseEffect)
		if len(useEffect) == 0 {
			useEffect = []byte("{}")
		}
		if err := st.UpsertItemDefinition(ctx, store.ItemDefinitionRow{
			ID:            d.ItemDefinitionID,
			PrimaryTag:    d.PrimaryTag,
			Tags:          d.Tags,
			StackLimit:    d.StackLimit,
			WeightMilli:   d.WeightMilli,
			Rarity:        d.Rarity,
			BaseValue:     d.BaseValue,
			SellPrice:     d.SellPrice,
			ConsumeHunger: d.ConsumeHunger,
			WasteOutput:   d.WasteOutput,
			IsInstance:    d.IsInstance,
			UseEffect:     useEffect,
		}); err != nil {
			return err
		}
	}
	return nil
}

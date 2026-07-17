package economy

import (
	"embed"
	"encoding/json"
	"fmt"
	"math/rand"
	"path"
	"sort"
)

//go:embed tables/*.json
var tableFS embed.FS

// Stock generation bounds (MVP 8.7 / 付録B.3).
const (
	minSlotQuantity = 1 // 各 slot の最小個数
	maxSlotQuantity = 5 // 各 slot の最大個数（1〜5）
)

// tableEntry is one weighted item_tag row of a Buyer Stock Definition.
type tableEntry struct {
	ItemTag string `json:"item_tag"`
	Weight  int64  `json:"weight"`
}

// StockTable is a Buyer Stock Definition (付録B.3). Stock is finite and drawn by
// weight; GuaranteedRareSlots is 0 by design — a draw with no rare at all is a
// valid outcome and must never be back-filled (AT-011 / AT-017).
type StockTable struct {
	InventoryTableID    string       `json:"inventory_table_id"`
	SlotCountMin        int          `json:"slot_count_min"`
	SlotCountMax        int          `json:"slot_count_max"`
	Entries             []tableEntry `json:"entries"`
	GuaranteedRareSlots int          `json:"guaranteed_rare_slots"`
}

// GeneratedStock is one generated stock row, before it gets a stock_entry_id.
// unit_price is fixed here and never recomputed at purchase time (3.4).
type GeneratedStock struct {
	ItemDefinitionID  string
	UnitPrice         int64
	RemainingQuantity int
}

// TagResolver resolves an item_tag to the candidate Item Definitions carrying
// it, and looks up a definition's base_value. itemdef.Catalog satisfies this.
type TagResolver interface {
	ByTag(tag string) []string // 該当タグの item_definition_id（順序は問わない）
	BaseValue(id string) (int64, bool)
}

// LoadTables reads and validates every embedded Buyer Stock Definition, keyed by
// inventory_table_id. Embedding keeps generation independent of the filesystem
// so the same binary always draws from the same tables.
func LoadTables() (map[string]StockTable, error) {
	files, err := tableFS.ReadDir("tables")
	if err != nil {
		return nil, fmt.Errorf("economy: read tables dir: %w", err)
	}
	tables := make(map[string]StockTable, len(files))
	for _, f := range files {
		raw, err := tableFS.ReadFile(path.Join("tables", f.Name()))
		if err != nil {
			return nil, fmt.Errorf("economy: read table %s: %w", f.Name(), err)
		}
		var t StockTable
		if err := json.Unmarshal(raw, &t); err != nil {
			return nil, fmt.Errorf("economy: parse table %s: %w", f.Name(), err)
		}
		if err := t.validate(); err != nil {
			return nil, fmt.Errorf("economy: table %s: %w", f.Name(), err)
		}
		if _, dup := tables[t.InventoryTableID]; dup {
			return nil, fmt.Errorf("economy: duplicate inventory_table_id %q", t.InventoryTableID)
		}
		tables[t.InventoryTableID] = t
	}
	if len(tables) == 0 {
		return nil, fmt.Errorf("economy: no buyer stock tables")
	}
	return tables, nil
}

func (t StockTable) validate() error {
	if t.InventoryTableID == "" {
		return fmt.Errorf("inventory_table_id is empty")
	}
	if t.SlotCountMin <= 0 || t.SlotCountMax < t.SlotCountMin {
		return fmt.Errorf("invalid slot_count range %d..%d", t.SlotCountMin, t.SlotCountMax)
	}
	if len(t.Entries) == 0 {
		return fmt.Errorf("no entries")
	}
	var total int64
	for _, e := range t.Entries {
		if e.ItemTag == "" {
			return fmt.Errorf("entry with empty item_tag")
		}
		if e.Weight <= 0 {
			return fmt.Errorf("entry %s weight must be > 0", e.ItemTag)
		}
		total += e.Weight
	}
	if total <= 0 {
		return fmt.Errorf("total weight must be > 0")
	}
	// レア保証は仕様上 0 のみ。>0 を許すと抽選後の補填を招く（AT-011 / AT-017）。
	if t.GuaranteedRareSlots != 0 {
		return fmt.Errorf("guaranteed_rare_slots must be 0 (レア保証なし), got %d", t.GuaranteedRareSlots)
	}
	return nil
}

// GenerateStock deterministically draws a Buyer's finite stock from the table
// and seed (付録B.3 / 09B 3.5). The same (table, seed, priceModifierBP) always
// yields the same (item_definition_id, unit_price, remaining_quantity) sequence
// — only the stock_entry_id UUIDs differ per registration, and those never feed
// back into the draw (決定性の破壊を防ぐ: 時刻/UUID/未シード乱数は使わない).
//
// All draws use integer PRNG APIs; no float ever touches the distribution (13.1).
func GenerateStock(t StockTable, seed int64, priceModifierBP int64, defs TagResolver) ([]GeneratedStock, error) {
	rng := rand.New(rand.NewSource(seed)) //nolint:gosec // 決定的再現が要件。暗号用途ではない（B.3）

	slotCount := t.SlotCountMin + rng.Intn(t.SlotCountMax-t.SlotCountMin+1)

	var totalWeight int64
	for _, e := range t.Entries {
		totalWeight += e.Weight
	}

	stock := make([]GeneratedStock, 0, slotCount)
	for i := 0; i < slotCount; i++ {
		tag := drawTag(t.Entries, totalWeight, rng)

		// 同一タグに複数 Definition があり得るので、id 昇順に整列してから抽選する。
		// マップ反復順に依存すると同一 seed でも結果がぶれる（決定性の破壊）。
		candidates := defs.ByTag(tag)
		if len(candidates) == 0 {
			// タグに対応する Definition が無いのはマスタとテーブルの不整合。
			// 黙って slot を落とすと在庫数が静かに減るので失敗させる。
			return nil, fmt.Errorf("economy: no item definition for tag %q (table %s)", tag, t.InventoryTableID)
		}
		sort.Strings(candidates)
		defID := candidates[rng.Intn(len(candidates))]

		baseValue, ok := defs.BaseValue(defID)
		if !ok {
			return nil, fmt.Errorf("economy: no base_value for %q", defID)
		}

		stock = append(stock, GeneratedStock{
			ItemDefinitionID:  defID,
			UnitPrice:         computeUnitPrice(baseValue, priceModifierBP, worldPriceModifierBP),
			RemainingQuantity: minSlotQuantity + rng.Intn(maxSlotQuantity-minSlotQuantity+1),
		})
	}
	// guaranteed_rare_slots=0: レアが 1 つも並ばない回をそのまま許容する。
	// ここでレアを補填してはならない（AT-011 / AT-017・09B 6章）。
	return stock, nil
}

// drawTag picks one item_tag by cumulative weight comparison. rng.Int63n keeps
// the draw in integer space (float 化しない・B.3 手順3).
func drawTag(entries []tableEntry, totalWeight int64, rng *rand.Rand) string {
	roll := rng.Int63n(totalWeight)
	var cumulative int64
	for _, e := range entries {
		cumulative += e.Weight
		if roll < cumulative {
			return e.ItemTag
		}
	}
	// 到達しない（roll < totalWeight）。防御的に最後の entry を返す。
	return entries[len(entries)-1].ItemTag
}

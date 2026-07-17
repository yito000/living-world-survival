package economy

import (
	"reflect"
	"testing"

	"living-world-survival/services/api/internal/itemdef"
)

// realCatalog loads the actual Item Definition master so the embedded tables are
// tested against the master they must agree with (item_tag のズレを検出する)。
func realCatalog(t *testing.T) *itemdef.Catalog {
	t.Helper()
	c, err := itemdef.Load("../../data/item_definitions.json")
	if err != nil {
		t.Fatalf("load item definitions: %v", err)
	}
	return c
}

func rareWeaponTable(t *testing.T) StockTable {
	t.Helper()
	tables, err := LoadTables()
	if err != nil {
		t.Fatalf("load tables: %v", err)
	}
	table, ok := tables["rare_weapon_buyer_v1"]
	if !ok {
		t.Fatal("rare_weapon_buyer_v1 not found")
	}
	return table
}

// 埋め込みテーブルが正本マスタと矛盾しないこと（存在しない item_tag を引かない）。
func TestTablesResolveAgainstMaster(t *testing.T) {
	catalog := realCatalog(t)
	tables, err := LoadTables()
	if err != nil {
		t.Fatalf("load tables: %v", err)
	}
	for id, table := range tables {
		for _, e := range table.Entries {
			if got := catalog.ByTag(e.ItemTag); len(got) == 0 {
				t.Errorf("table %s: item_tag %q に対応する Item Definition が無い", id, e.ItemTag)
			}
		}
	}
}

// 09B 5章 Unit / AT-011: 同一 (table_id, seed) は同一在庫列を生成する。
func TestGenerateStockDeterministic(t *testing.T) {
	catalog := realCatalog(t)
	table := rareWeaponTable(t)

	for _, seed := range []int64{0, 1, 42, -7, 1 << 40} {
		first, err := GenerateStock(table, seed, 10000, catalog)
		if err != nil {
			t.Fatalf("seed %d: %v", seed, err)
		}
		for i := 0; i < 5; i++ {
			again, err := GenerateStock(table, seed, 10000, catalog)
			if err != nil {
				t.Fatalf("seed %d rerun: %v", seed, err)
			}
			if !reflect.DeepEqual(first, again) {
				t.Fatalf("seed %d: 在庫が再現しない\n1回目: %+v\n%d回目: %+v", seed, first, i+2, again)
			}
		}
	}
}

// 異なる seed は（少なくとも一部で）異なる在庫を生む＝seed が実際に効いている。
func TestGenerateStockVariesBySeed(t *testing.T) {
	catalog := realCatalog(t)
	table := rareWeaponTable(t)

	base, err := GenerateStock(table, 1, 10000, catalog)
	if err != nil {
		t.Fatal(err)
	}
	differs := false
	for seed := int64(2); seed < 30; seed++ {
		other, err := GenerateStock(table, seed, 10000, catalog)
		if err != nil {
			t.Fatal(err)
		}
		if !reflect.DeepEqual(base, other) {
			differs = true
			break
		}
	}
	if !differs {
		t.Error("seed を変えても在庫が変わらない: seed が抽選に効いていない")
	}
}

// 在庫は有限（slot 数・各数量が定義範囲内）であること（付録B.3 / AT-011）。
func TestGenerateStockIsFinite(t *testing.T) {
	catalog := realCatalog(t)
	table := rareWeaponTable(t)

	for seed := int64(0); seed < 200; seed++ {
		stock, err := GenerateStock(table, seed, 10000, catalog)
		if err != nil {
			t.Fatalf("seed %d: %v", seed, err)
		}
		if len(stock) < table.SlotCountMin || len(stock) > table.SlotCountMax {
			t.Fatalf("seed %d: slot 数 %d が範囲 %d..%d 外", seed, len(stock), table.SlotCountMin, table.SlotCountMax)
		}
		for _, s := range stock {
			if s.RemainingQuantity < minSlotQuantity || s.RemainingQuantity > maxSlotQuantity {
				t.Fatalf("seed %d: 数量 %d が範囲 %d..%d 外", seed, s.RemainingQuantity, minSlotQuantity, maxSlotQuantity)
			}
			if s.UnitPrice < minUnitPrice {
				t.Fatalf("seed %d: unit_price %d が最低価格未満", seed, s.UnitPrice)
			}
		}
	}
}

// AT-011 / AT-017: レア保証なし。レアが 1 つも並ばない回が実在すること。
// （レアを後から強制挿入する実装だとこのテストが落ちる）
func TestGenerateStockAllowsNoRare(t *testing.T) {
	catalog := realCatalog(t)
	table := rareWeaponTable(t)

	isRare := func(defID string) bool {
		d, ok := catalog.Get(defID)
		return ok && d.Rarity >= 2
	}

	noRareRuns := 0
	for seed := int64(0); seed < 200; seed++ {
		stock, err := GenerateStock(table, seed, 10000, catalog)
		if err != nil {
			t.Fatalf("seed %d: %v", seed, err)
		}
		rare := false
		for _, s := range stock {
			if isRare(s.ItemDefinitionID) {
				rare = true
				break
			}
		}
		if !rare {
			noRareRuns++
		}
	}
	if noRareRuns == 0 {
		t.Error("200 seed すべてにレアが並んだ: レア保証なし（guaranteed_rare_slots=0）に反する")
	}
	t.Logf("レア無しの回: %d / 200", noRareRuns)
}

// 抽選が weight に沿うこと: 60:25:8:2 の table で common が rare を明確に上回る。
func TestGenerateStockFollowsWeights(t *testing.T) {
	catalog := realCatalog(t)
	table := rareWeaponTable(t)

	counts := map[string]int{}
	for seed := int64(0); seed < 2000; seed++ {
		stock, err := GenerateStock(table, seed, 10000, catalog)
		if err != nil {
			t.Fatalf("seed %d: %v", seed, err)
		}
		for _, s := range stock {
			counts[s.ItemDefinitionID]++
		}
	}
	// weight 60 の weapon.hunting.basic → stone_spear、weight 8 の weapon.rare → rare_weapon。
	common, rare := counts["stone_spear"], counts["rare_weapon"]
	if common == 0 {
		t.Fatal("weight 60 の item が 1 度も出ていない")
	}
	if rare == 0 {
		t.Fatal("weight 8 の item が 1 度も出ていない")
	}
	if common <= rare {
		t.Errorf("weight 60 (%d回) が weight 8 (%d回) を上回っていない", common, rare)
	}
	t.Logf("抽選分布: %+v", counts)
}

// 生成された unit_price が base_value と modifier から決まること（3.4 と整合）。
func TestGenerateStockAppliesModifier(t *testing.T) {
	catalog := realCatalog(t)
	table := rareWeaponTable(t)

	normal, err := GenerateStock(table, 99, 10000, catalog)
	if err != nil {
		t.Fatal(err)
	}
	doubled, err := GenerateStock(table, 99, 20000, catalog)
	if err != nil {
		t.Fatal(err)
	}
	if len(normal) != len(doubled) {
		t.Fatalf("modifier で slot 数が変わった: %d vs %d", len(normal), len(doubled))
	}
	for i := range normal {
		// modifier は抽選に影響しない（同 seed なら同じ品揃え）。
		if normal[i].ItemDefinitionID != doubled[i].ItemDefinitionID {
			t.Fatalf("slot %d: modifier が抽選を変えた", i)
		}
		base, _ := catalog.BaseValue(normal[i].ItemDefinitionID)
		if want := computeUnitPrice(base, 10000, worldPriceModifierBP); normal[i].UnitPrice != want {
			t.Errorf("slot %d: unit_price = %d, want %d", i, normal[i].UnitPrice, want)
		}
		if want := computeUnitPrice(base, 20000, worldPriceModifierBP); doubled[i].UnitPrice != want {
			t.Errorf("slot %d (×2): unit_price = %d, want %d", i, doubled[i].UnitPrice, want)
		}
	}
}

package itemdef

import (
	"path/filepath"
	"testing"
)

// masterPath resolves the repo-supplied master JSON relative to this package.
func masterPath() string {
	return filepath.Join("..", "..", "data", "item_definitions.json")
}

func TestLoadMaster(t *testing.T) {
	cat, err := Load(masterPath())
	if err != nil {
		t.Fatalf("Load: %v", err)
	}
	if cat.Len() != 18 {
		t.Fatalf("expected 18 definitions (MVP 7.2), got %d", cat.Len())
	}

	// Spot-check a couple of entries and the milli-integer weight convention.
	stone, ok := cat.Get("stone")
	if !ok {
		t.Fatal("stone missing")
	}
	if stone.WeightMilli != 1000 || stone.StackLimit != 50 {
		t.Errorf("stone: got weight=%d stack=%d", stone.WeightMilli, stone.StackLimit)
	}
	rareWeapon, ok := cat.Get("rare_weapon")
	if !ok {
		t.Fatal("rare_weapon missing")
	}
	if rareWeapon.Rarity != 3 {
		t.Errorf("rare_weapon rarity: got %d want 3", rareWeapon.Rarity)
	}
	// cooked_meat carries a use_effect (Hunger+30) and the M3 survival fields.
	cooked, _ := cat.Get("cooked_meat")
	if len(cooked.UseEffect) == 0 || string(cooked.UseEffect) == "{}" {
		t.Errorf("cooked_meat should have a use_effect, got %q", string(cooked.UseEffect))
	}
	if cooked.ConsumeHunger != 30 || cooked.WasteOutput != 1 {
		t.Errorf("cooked_meat: got consume_hunger=%d waste_output=%d want 30/1", cooked.ConsumeHunger, cooked.WasteOutput)
	}
	// luxury_food produces double waste on cooking (7.2 / 8.6-2).
	if lux, _ := cat.Get("luxury_food"); lux.WasteOutput != 2 || lux.ConsumeHunger != 30 {
		t.Errorf("luxury_food: got waste_output=%d consume_hunger=%d want 2/30", lux.WasteOutput, lux.ConsumeHunger)
	}
	// Weapons/tools are quality/durability-bearing individuals (item_instances).
	if !rareWeapon.IsInstance || rareWeapon.PrimaryTag != "weapon.rare" {
		t.Errorf("rare_weapon: is_instance=%v primary_tag=%q", rareWeapon.IsInstance, rareWeapon.PrimaryTag)
	}
	if stone.IsInstance {
		t.Errorf("stone should be a stackable, not an instance")
	}
	if stone.PrimaryTag != "resource.stone" {
		t.Errorf("stone primary_tag: got %q", stone.PrimaryTag)
	}
}

func TestValidationRejectsBadDefinitions(t *testing.T) {
	cases := []struct {
		name string
		defs []Definition
	}{
		{"empty id", []Definition{{ItemDefinitionID: "", StackLimit: 1}}},
		{"zero stack", []Definition{{ItemDefinitionID: "x", StackLimit: 0}}},
		{"negative weight", []Definition{{ItemDefinitionID: "x", StackLimit: 1, WeightMilli: -1}}},
		{"bad rarity", []Definition{{ItemDefinitionID: "x", StackLimit: 1, Rarity: 4}}},
		{"duplicate id", []Definition{
			{ItemDefinitionID: "x", StackLimit: 1},
			{ItemDefinitionID: "x", StackLimit: 1},
		}},
		{"invalid use_effect json", []Definition{{ItemDefinitionID: "x", StackLimit: 1, UseEffect: []byte("{not json")}}},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			if _, err := newCatalog(tc.defs); err == nil {
				t.Fatalf("expected validation error for %s", tc.name)
			}
		})
	}
}

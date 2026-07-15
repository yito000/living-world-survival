using SurvivalWorld.Items;

namespace SurvivalWorld.Shared.MasterData
{
    public static class M3ItemDefinitions
    {
        public static InMemoryItemDefinitionCatalog CreateCatalog()
        {
            return new InMemoryItemDefinitionCatalog(new[]
            {
                Define("stone", new[] { "resource.stone" }, 50, 1.0f, 0),
                Define("iron_ore", new[] { "resource.ore.iron" }, 30, 1.5f, 0),
                Define("rare_ore", new[] { "resource.ore.rare" }, 10, 1.5f, 2),
                Define("wood", new[] { "resource.wood" }, 30, 0.8f, 0),
                Define("iron_ingot", new[] { "material.ingot.iron" }, 20, 1.2f, 0),
                Define("rare_ingot", new[] { "material.ingot.rare" }, 10, 1.2f, 2),
                Define("leather", new[] { "material.leather" }, 20, 0.5f, 0),
                Define("bone", new[] { "material.bone" }, 20, 0.5f, 0),
                Define("stone_spear", new[] { "weapon.spear.stone" }, 1, 3.0f, 0),
                Define("raw_meat", new[] { "food.raw_meat" }, 10, 1.0f, 0),
                Define("rare_meat", new[] { "food.rare_meat" }, 5, 1.0f, 2),
                Define("cooked_meat", new[] { "food.cooked_meat" }, 10, 0.8f, 0, ItemUseEffect.Hunger(30)),
                Define("food_waste", new[] { "food.waste", "waste.food" }, 20, 0.3f, 0),
                Define("stone_pickaxe", new[] { "tool.mining" }, 1, 4.0f, 0),
                Define("iron_hunting_spear", new[] { "weapon.spear.iron" }, 1, 5.0f, 0),
                Define("luxury_food", new[] { "food.luxury" }, 5, 0.8f, 2, ItemUseEffect.HungerAndWaste(30, 2)),
                Define("potato", new[] { "food.crop.potato" }, 20, 0.4f, 0),
                Define("rare_weapon", new[] { "weapon.rare" }, 1, 5.0f, 3)
            });
        }

        private static ItemDefinitionData Define(string id, string[] tags, int stackLimit, float weight, int rarity)
        {
            return Define(id, tags, stackLimit, weight, rarity, ItemUseEffect.None);
        }

        private static ItemDefinitionData Define(string id, string[] tags, int stackLimit, float weight, int rarity, ItemUseEffect effect)
        {
            return new ItemDefinitionData(id, tags, stackLimit, weight, rarity, 0, effect);
        }
    }
}

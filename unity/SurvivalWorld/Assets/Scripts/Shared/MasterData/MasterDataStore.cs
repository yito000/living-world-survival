using System;
using System.Collections.Generic;

namespace SurvivalWorld.Shared.MasterData
{
    public sealed class MasterDataStore
    {
        private readonly Dictionary<string, RecipeDefinition> recipes = new Dictionary<string, RecipeDefinition>(StringComparer.Ordinal);
        private readonly Dictionary<string, ResourceNodeDefinition> resourceNodes = new Dictionary<string, ResourceNodeDefinition>(StringComparer.Ordinal);
        private readonly Dictionary<string, DropTableDefinition> dropTables = new Dictionary<string, DropTableDefinition>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, RecipeDefinition> Recipes => recipes;
        public IReadOnlyDictionary<string, ResourceNodeDefinition> ResourceNodes => resourceNodes;
        public IReadOnlyDictionary<string, DropTableDefinition> DropTables => dropTables;

        public static MasterDataStore CreateM3Defaults()
        {
            var store = new MasterDataStore();
            store.AddResourceNode(new ResourceNodeDefinition("stone", "stone", 3, 1, "tool.mining"));
            store.AddResourceNode(new ResourceNodeDefinition("iron", "iron_ore", 2, 2, "tool.mining"));
            store.AddResourceNode(new ResourceNodeDefinition("rare", "rare_ore", 1, 3, "tool.mining"));

            store.AddRecipe(new RecipeDefinition(
                "stone_pickaxe",
                "forge",
                RecipeKind.Craft,
                new[] { new ItemStack("stone", 5), new ItemStack("wood", 2) },
                new[] { new ItemStack("stone_pickaxe", 1) },
                30));
            store.AddRecipe(new RecipeDefinition(
                "stone_spear",
                "anvil",
                RecipeKind.Craft,
                new[] { new ItemStack("stone", 3), new ItemStack("wood", 2) },
                new[] { new ItemStack("stone_spear", 1) },
                20));
            store.AddRecipe(new RecipeDefinition(
                "iron_ingot",
                "forge",
                RecipeKind.Smelting,
                new[] { new ItemStack("iron_ore", 2) },
                new[] { new ItemStack("iron_ingot", 1) },
                40));
            store.AddRecipe(new RecipeDefinition(
                "rare_ingot",
                "forge",
                RecipeKind.Smelting,
                new[] { new ItemStack("rare_ore", 2) },
                new[] { new ItemStack("rare_ingot", 1) },
                60));
            store.AddRecipe(new RecipeDefinition(
                "iron_hunting_spear",
                "anvil",
                RecipeKind.Craft,
                new[] { new ItemStack("iron_ingot", 3), new ItemStack("wood", 1), new ItemStack("leather", 1) },
                new[] { new ItemStack("iron_hunting_spear", 1) },
                60,
                "iron_spear"));
            store.AddRecipe(new RecipeDefinition(
                "iron_spear_research",
                "forge",
                RecipeKind.Development,
                new[] { new ItemStack("iron_ore", 5), new ItemStack("rare_ore", 1) },
                Array.Empty<ItemStack>(),
                120,
                string.Empty,
                "iron_spear"));
            store.AddRecipe(new RecipeDefinition(
                "rare_weapon_craft",
                "anvil",
                RecipeKind.Craft,
                new[] { new ItemStack("rare_ingot", 3), new ItemStack("iron_ingot", 5) },
                new[] { new ItemStack("rare_weapon", 1) },
                90,
                "rare_weapon"));
            store.AddRecipe(new RecipeDefinition(
                "cook_raw_meat",
                "cooking",
                RecipeKind.Cooking,
                new[] { new ItemStack("raw_meat", 1) },
                new[] { new ItemStack("cooked_meat", 1), new ItemStack("food_waste", 1) },
                20));
            store.AddRecipe(new RecipeDefinition(
                "luxury_food",
                "cooking",
                RecipeKind.Cooking,
                new[] { new ItemStack("raw_meat", 1), new ItemStack("rare_meat", 1) },
                new[] { new ItemStack("luxury_food", 1), new ItemStack("food_waste", 2) },
                40));

            store.AddDropTable(new DropTableDefinition(
                "deer",
                new[] { new ItemStack("raw_meat", 1), new ItemStack("leather", 1), new ItemStack("bone", 1) },
                Array.Empty<ItemStack>()));
            store.AddDropTable(new DropTableDefinition(
                "rare_deer",
                new[] { new ItemStack("raw_meat", 1), new ItemStack("leather", 1), new ItemStack("bone", 1) },
                new[] { new ItemStack("rare_meat", 1), new ItemStack("rare_ore", 1) }));
            return store;
        }

        public void AddRecipe(RecipeDefinition recipe)
        {
            if (recipe != null && !string.IsNullOrWhiteSpace(recipe.RecipeId))
            {
                recipes[recipe.RecipeId] = recipe;
            }
        }

        public void AddResourceNode(ResourceNodeDefinition node)
        {
            if (node != null && !string.IsNullOrWhiteSpace(node.ResourceType))
            {
                resourceNodes[node.ResourceType] = node;
            }
        }

        public void AddDropTable(DropTableDefinition table)
        {
            if (table != null && !string.IsNullOrWhiteSpace(table.Species))
            {
                dropTables[table.Species] = table;
            }
        }

        public bool TryGetRecipe(string recipeId, out RecipeDefinition recipe)
        {
            return recipes.TryGetValue(recipeId ?? string.Empty, out recipe);
        }

        public bool TryGetResourceNode(string resourceType, out ResourceNodeDefinition node)
        {
            return resourceNodes.TryGetValue(resourceType ?? string.Empty, out node);
        }

        public bool TryGetDropTable(string species, out DropTableDefinition table)
        {
            return dropTables.TryGetValue(species ?? string.Empty, out table);
        }
    }
}

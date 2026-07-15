using System;
using System.Collections.Generic;
using System.Linq;

namespace SurvivalWorld.Shared.MasterData
{
    public enum RecipeKind
    {
        Craft,
        Development,
        Cooking,
        Smelting
    }

    public sealed class RecipeDefinition
    {
        public RecipeDefinition(
            string recipeId,
            string stationType,
            RecipeKind kind,
            IEnumerable<ItemStack> ingredients,
            IEnumerable<ItemStack> outputs,
            int durationSeconds,
            string requiredBlueprintId = "",
            string unlockedBlueprintId = "")
        {
            RecipeId = recipeId ?? string.Empty;
            StationType = stationType ?? string.Empty;
            Kind = kind;
            Ingredients = ingredients == null ? Array.Empty<ItemStack>() : ingredients.ToArray();
            Outputs = outputs == null ? Array.Empty<ItemStack>() : outputs.ToArray();
            DurationSeconds = Math.Max(0, durationSeconds);
            RequiredBlueprintId = requiredBlueprintId ?? string.Empty;
            UnlockedBlueprintId = unlockedBlueprintId ?? string.Empty;
        }

        public string RecipeId { get; }
        public string StationType { get; }
        public RecipeKind Kind { get; }
        public IReadOnlyList<ItemStack> Ingredients { get; }
        public IReadOnlyList<ItemStack> Outputs { get; }
        public int DurationSeconds { get; }
        public string RequiredBlueprintId { get; }
        public string UnlockedBlueprintId { get; }
        public bool RequiresBlueprint => !string.IsNullOrWhiteSpace(RequiredBlueprintId);
        public bool UnlocksBlueprint => !string.IsNullOrWhiteSpace(UnlockedBlueprintId);
    }
}

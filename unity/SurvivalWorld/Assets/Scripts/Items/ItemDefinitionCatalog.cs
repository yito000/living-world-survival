using System;
using System.Collections.Generic;
using System.Linq;

namespace SurvivalWorld.Items
{
    public readonly struct ItemDefinitionData
    {
        public ItemDefinitionData(string itemDefinitionId, IEnumerable<string> tags, int stackLimit, float weight, int rarity, long baseValue, ItemUseEffect useEffect)
        {
            ItemDefinitionId = itemDefinitionId ?? string.Empty;
            Tags = tags == null ? Array.Empty<string>() : tags.ToArray();
            StackLimit = stackLimit;
            Weight = weight;
            Rarity = rarity;
            BaseValue = baseValue;
            UseEffect = useEffect;
        }

        public string ItemDefinitionId { get; }
        public string[] Tags { get; }
        public int StackLimit { get; }
        public float Weight { get; }
        public int Rarity { get; }
        public long BaseValue { get; }
        public ItemUseEffect UseEffect { get; }
    }

    public interface IItemDefinitionCatalog
    {
        IReadOnlyList<ItemDefinitionData> Definitions { get; }
        bool TryGet(string itemDefinitionId, out ItemDefinitionData definition);
    }

    public sealed class InMemoryItemDefinitionCatalog : IItemDefinitionCatalog
    {
        private readonly Dictionary<string, ItemDefinitionData> definitions;
        private readonly List<ItemDefinitionData> orderedDefinitions;

        public InMemoryItemDefinitionCatalog(IEnumerable<ItemDefinitionData> definitions)
        {
            if (definitions == null)
            {
                throw new ArgumentNullException(nameof(definitions));
            }

            this.definitions = new Dictionary<string, ItemDefinitionData>(StringComparer.Ordinal);
            orderedDefinitions = new List<ItemDefinitionData>();
            foreach (ItemDefinitionData definition in definitions)
            {
                if (string.IsNullOrWhiteSpace(definition.ItemDefinitionId))
                {
                    throw new ArgumentException("Item definition id must not be empty.", nameof(definitions));
                }

                this.definitions[definition.ItemDefinitionId] = definition;
                orderedDefinitions.Add(definition);
            }
        }

        public IReadOnlyList<ItemDefinitionData> Definitions => orderedDefinitions;

        public bool TryGet(string itemDefinitionId, out ItemDefinitionData definition)
        {
            return definitions.TryGetValue(itemDefinitionId ?? string.Empty, out definition);
        }
    }

    public static class ItemDefinitionCatalog
    {
        private static readonly ItemDefinitionData[] mvpDefinitions =
        {
            Define("stone", "resource.stone", 50, 1.0f, 0),
            Define("iron_ore", "resource.ore.iron", 30, 1.5f, 0),
            Define("rare_ore", "resource.ore.rare", 10, 1.5f, 2),
            Define("wood", "resource.wood", 30, 0.8f, 0),
            Define("iron_ingot", "material.ingot.iron", 20, 1.2f, 0),
            Define("rare_ingot", "material.ingot.rare", 10, 1.2f, 2),
            Define("leather", "material.leather", 20, 0.5f, 0),
            Define("bone", "material.bone", 20, 0.5f, 0),
            Define("stone_spear", "weapon.spear.stone", 1, 3.0f, 0),
            Define("raw_meat", "food.raw_meat", 10, 1.0f, 0),
            Define("rare_meat", "food.rare_meat", 5, 1.0f, 2),
            Define("cooked_meat", "food.cooked_meat", 10, 0.8f, 0, ItemUseEffect.Hunger(30)),
            Define("food_waste", "food.waste", 20, 0.3f, 0),
            Define("stone_pickaxe", "tool.mining", 1, 4.0f, 0),
            Define("iron_hunting_spear", "weapon.spear.iron", 1, 5.0f, 0),
            Define("luxury_food", "food.luxury", 5, 0.8f, 2, ItemUseEffect.HungerAndWaste(30, 2)),
            Define("decorative_weapon", "weapon.decorative", 1, 6.0f, 2),
            Define("rare_weapon", "weapon.rare", 1, 5.0f, 3)
        };

        public static IReadOnlyList<ItemDefinitionData> MvpDefinitions => mvpDefinitions;

        public static InMemoryItemDefinitionCatalog CreateMvpCatalog()
        {
            return new InMemoryItemDefinitionCatalog(mvpDefinitions);
        }

        public static InMemoryItemDefinitionCatalog FromAssets(IEnumerable<ItemDefinition> assets)
        {
            if (assets == null)
            {
                throw new ArgumentNullException(nameof(assets));
            }

            return new InMemoryItemDefinitionCatalog(assets.Where(asset => asset != null).Select(asset => asset.ToData()));
        }

        private static ItemDefinitionData Define(string id, string tag, int stackLimit, float weight, int rarity)
        {
            return Define(id, tag, stackLimit, weight, rarity, ItemUseEffect.None);
        }

        private static ItemDefinitionData Define(string id, string tag, int stackLimit, float weight, int rarity, ItemUseEffect useEffect)
        {
            return new ItemDefinitionData(id, new[] { tag }, stackLimit, weight, rarity, 0, useEffect);
        }
    }
}

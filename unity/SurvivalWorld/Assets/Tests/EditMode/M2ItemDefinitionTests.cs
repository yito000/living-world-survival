using System.IO;
using System.Linq;
using NUnit.Framework;
using SurvivalWorld.Items;
using UnityEditor;

namespace SurvivalWorld.Tests
{
    public sealed class M2ItemDefinitionTests
    {
        [Test]
        public void MvpCatalogContainsExpectedEighteenDefinitions()
        {
            var catalog = ItemDefinitionCatalog.CreateMvpCatalog();

            Assert.AreEqual(18, catalog.Definitions.Count);
            Assert.IsTrue(catalog.TryGet("stone", out ItemDefinitionData stone));
            Assert.AreEqual(50, stone.StackLimit);
            Assert.AreEqual(1.0f, stone.Weight, 0.0001f);
            Assert.AreEqual(0, stone.Rarity);
            Assert.IsTrue(catalog.TryGet("rare_weapon", out ItemDefinitionData rareWeapon));
            Assert.AreEqual(3, rareWeapon.Rarity);
            Assert.IsTrue(catalog.TryGet("luxury_food", out ItemDefinitionData luxuryFood));
            Assert.AreEqual(ItemUseEffectType.HungerAndWaste, luxuryFood.UseEffect.Type);
        }

        [Test]
        public void GeneratedItemDefinitionAssetsMatchMvpDefaults()
        {
            string[] guids = AssetDatabase.FindAssets("t:ItemDefinition", new[] { "Assets/Data/Items" });
            ItemDefinition[] assets = guids
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<ItemDefinition>)
                .Where(asset => asset != null)
                .ToArray();

            Assert.AreEqual(18, assets.Length);
            var actual = ItemDefinitionCatalog.FromAssets(assets).Definitions;
            Assert.IsTrue(ItemDefinitionJsonImporter.Matches(ItemDefinitionCatalog.MvpDefinitions, actual, out string mismatch), mismatch);
        }

        [Test]
        public void OptionalSourceJsonMatchesDefaultsWhenPresent()
        {
            const string sourceJsonPath = "services/api/data/item_definitions.json";
            if (!File.Exists(sourceJsonPath))
            {
                Assert.Ignore("WSL2 source item_definitions.json is not present in this checkout.");
            }

            var parsed = ItemDefinitionJsonImporter.Parse(File.ReadAllText(sourceJsonPath));
            Assert.IsTrue(ItemDefinitionJsonImporter.Matches(ItemDefinitionCatalog.MvpDefinitions, parsed, out string mismatch), mismatch);
        }
    }
}

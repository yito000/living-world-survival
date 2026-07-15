using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace SurvivalWorld.Items
{
    public static class ItemDefinitionJsonImporter
    {
        public static IReadOnlyList<ItemDefinitionData> Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return Array.Empty<ItemDefinitionData>();
            }

            string trimmed = json.Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                trimmed = "{\"items\":" + trimmed + "}";
            }

            ItemDefinitionJsonWrapper wrapper = JsonUtility.FromJson<ItemDefinitionJsonWrapper>(trimmed);
            if (wrapper == null || wrapper.items == null)
            {
                return Array.Empty<ItemDefinitionData>();
            }

            var result = new List<ItemDefinitionData>(wrapper.items.Length);
            for (int i = 0; i < wrapper.items.Length; i++)
            {
                ItemDefinitionJsonRecord record = wrapper.items[i];
                if (record == null)
                {
                    continue;
                }

                string id = FirstNonEmpty(record.item_definition_id, record.id);
                int stackLimit = record.stack_limit > 0 ? record.stack_limit : record.stackLimit;
                float weight = record.weight;
                if (weight <= 0f && record.weight_milli > 0)
                {
                    weight = record.weight_milli / 1000f;
                }

                long baseValue = record.base_value != 0 ? record.base_value : record.baseValue;
                result.Add(new ItemDefinitionData(id, record.tags, stackLimit, weight, record.rarity, baseValue, ParseEffect(record.use_effect)));
            }

            return result;
        }

        public static bool Matches(IReadOnlyList<ItemDefinitionData> expected, IReadOnlyList<ItemDefinitionData> actual, out string mismatch)
        {
            mismatch = string.Empty;
            if (expected == null || actual == null)
            {
                mismatch = "Definition list is null.";
                return false;
            }

            var actualById = new Dictionary<string, ItemDefinitionData>(StringComparer.Ordinal);
            for (int i = 0; i < actual.Count; i++)
            {
                actualById[actual[i].ItemDefinitionId] = actual[i];
            }

            for (int i = 0; i < expected.Count; i++)
            {
                ItemDefinitionData expectedDefinition = expected[i];
                if (!actualById.TryGetValue(expectedDefinition.ItemDefinitionId, out ItemDefinitionData actualDefinition))
                {
                    mismatch = "Missing item definition: " + expectedDefinition.ItemDefinitionId;
                    return false;
                }

                if (expectedDefinition.StackLimit != actualDefinition.StackLimit ||
                    Math.Abs(expectedDefinition.Weight - actualDefinition.Weight) > 0.0001f ||
                    expectedDefinition.Rarity != actualDefinition.Rarity)
                {
                    mismatch = string.Format(
                        CultureInfo.InvariantCulture,
                        "Definition mismatch for {0}. Expected stack={1}, weight={2}, rarity={3}; actual stack={4}, weight={5}, rarity={6}.",
                        expectedDefinition.ItemDefinitionId,
                        expectedDefinition.StackLimit,
                        expectedDefinition.Weight,
                        expectedDefinition.Rarity,
                        actualDefinition.StackLimit,
                        actualDefinition.Weight,
                        actualDefinition.Rarity);
                    return false;
                }
            }

            return true;
        }

        private static string FirstNonEmpty(string first, string second)
        {
            return string.IsNullOrWhiteSpace(first) ? second ?? string.Empty : first;
        }

        private static ItemUseEffect ParseEffect(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return ItemUseEffect.None;
            }

            string normalized = value.Trim().ToLowerInvariant();
            if (normalized == "hunger+30" || normalized == "hunger_30")
            {
                return ItemUseEffect.Hunger(30);
            }

            if (normalized == "hunger+30_wastex2" || normalized == "hunger_30_waste_2")
            {
                return ItemUseEffect.HungerAndWaste(30, 2);
            }

            return ItemUseEffect.None;
        }

        [Serializable]
        private sealed class ItemDefinitionJsonWrapper
        {
            public ItemDefinitionJsonRecord[] items;
        }

        [Serializable]
        private sealed class ItemDefinitionJsonRecord
        {
            public string item_definition_id;
            public string id;
            public string[] tags;
            public int stack_limit;
            public int stackLimit;
            public float weight;
            public int weight_milli;
            public int rarity;
            public long base_value;
            public long baseValue;
            public string use_effect;
        }
    }
}

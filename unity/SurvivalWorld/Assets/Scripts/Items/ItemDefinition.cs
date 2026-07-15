using System;
using UnityEngine;

namespace SurvivalWorld.Items
{
    [CreateAssetMenu(fileName = "ItemDefinition", menuName = "Survival World/Item Definition")]
    public sealed class ItemDefinition : ScriptableObject
    {
        [SerializeField] private string itemDefinitionId = string.Empty;
        [SerializeField] private string[] tags = Array.Empty<string>();
        [SerializeField] private int stackLimit = 1;
        [SerializeField] private float weight = 1f;
        [SerializeField] private int rarity;
        [SerializeField] private long baseValue;
        [SerializeField] private ItemUseEffect useEffect;

        public string ItemDefinitionId => itemDefinitionId;
        public string[] Tags => tags;
        public int StackLimit => stackLimit;
        public float Weight => weight;
        public int Rarity => rarity;
        public long BaseValue => baseValue;
        public ItemUseEffect UseEffect => useEffect;

        public void Configure(ItemDefinitionData data)
        {
            itemDefinitionId = data.ItemDefinitionId;
            tags = data.Tags;
            stackLimit = data.StackLimit;
            weight = data.Weight;
            rarity = data.Rarity;
            baseValue = data.BaseValue;
            useEffect = data.UseEffect;
        }

        public ItemDefinitionData ToData()
        {
            return new ItemDefinitionData(itemDefinitionId, tags, stackLimit, weight, rarity, baseValue, useEffect);
        }

        public bool HasTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag) || tags == null)
            {
                return false;
            }

            for (int i = 0; i < tags.Length; i++)
            {
                if (string.Equals(tags[i], tag, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}

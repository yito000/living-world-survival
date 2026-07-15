using System;
using UnityEngine;

namespace SurvivalWorld.Items
{
    public enum ItemUseEffectType
    {
        None = 0,
        Hunger = 1,
        HungerAndWaste = 2
    }

    [Serializable]
    public struct ItemUseEffect
    {
        [SerializeField] private ItemUseEffectType type;
        [SerializeField] private int hungerDelta;
        [SerializeField] private int wasteQuantity;

        public ItemUseEffect(ItemUseEffectType type, int hungerDelta, int wasteQuantity)
        {
            this.type = type;
            this.hungerDelta = hungerDelta;
            this.wasteQuantity = wasteQuantity;
        }

        public ItemUseEffectType Type => type;
        public int HungerDelta => hungerDelta;
        public int WasteQuantity => wasteQuantity;
        public bool HasEffect => type != ItemUseEffectType.None;

        public static ItemUseEffect None => new ItemUseEffect(ItemUseEffectType.None, 0, 0);

        public static ItemUseEffect Hunger(int delta)
        {
            return new ItemUseEffect(ItemUseEffectType.Hunger, delta, 0);
        }

        public static ItemUseEffect HungerAndWaste(int hungerDelta, int wasteQuantity)
        {
            return new ItemUseEffect(ItemUseEffectType.HungerAndWaste, hungerDelta, wasteQuantity);
        }
    }
}

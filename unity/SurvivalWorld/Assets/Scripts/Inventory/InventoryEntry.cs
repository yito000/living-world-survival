using System;

namespace SurvivalWorld.Inventory
{
    [Serializable]
    public struct InventoryEntry
    {
        public int SlotIndex;
        public string ItemDefinitionId;
        public string ItemInstanceId;
        public int Quantity;
        public int Reserved;

        public InventoryEntry(int slotIndex, string itemDefinitionId, string itemInstanceId, int quantity, int reserved)
        {
            SlotIndex = slotIndex;
            ItemDefinitionId = itemDefinitionId ?? string.Empty;
            ItemInstanceId = itemInstanceId ?? string.Empty;
            Quantity = quantity;
            Reserved = reserved;
        }

        public int AvailableQuantity => Math.Max(0, Quantity - Reserved);
    }
}

using System.Collections.Generic;
using SurvivalWorld.Inventory;

namespace SurvivalWorld.Client.UI
{
    public sealed class InventoryViewModel
    {
        private readonly List<InventorySlotViewState> slots = new List<InventorySlotViewState>();

        public IReadOnlyList<InventorySlotViewState> Slots => slots;

        public void ApplySnapshot(InventorySnapshot snapshot)
        {
            slots.Clear();
            if (snapshot == null)
            {
                return;
            }

            for (int i = 0; i < snapshot.Entries.Count; i++)
            {
                InventoryEntry entry = snapshot.Entries[i];
                slots.Add(new InventorySlotViewState(entry.SlotIndex, entry.ItemDefinitionId, entry.Quantity, entry.Reserved));
            }
        }
    }

    public readonly struct InventorySlotViewState
    {
        public InventorySlotViewState(int slotIndex, string itemDefinitionId, int quantity, int reserved)
        {
            SlotIndex = slotIndex;
            ItemDefinitionId = itemDefinitionId ?? string.Empty;
            Quantity = quantity;
            Reserved = reserved;
        }

        public int SlotIndex { get; }
        public string ItemDefinitionId { get; }
        public int Quantity { get; }
        public int Reserved { get; }
        public int Available => System.Math.Max(0, Quantity - Reserved);
    }
}

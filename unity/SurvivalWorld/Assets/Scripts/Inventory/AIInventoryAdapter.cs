using SurvivalWorld.Items;

namespace SurvivalWorld.Inventory
{
    public sealed class AIInventoryAdapter
    {
        private readonly InventoryService inventoryService;
        private readonly InventoryOwner owner;
        private readonly IItemDefinitionCatalog catalog;

        public AIInventoryAdapter(InventoryService inventoryService, InventoryOwner owner)
            : this(inventoryService, owner, null)
        {
        }

        public AIInventoryAdapter(InventoryService inventoryService, InventoryOwner owner, IItemDefinitionCatalog catalog)
        {
            this.inventoryService = inventoryService ?? throw new System.ArgumentNullException(nameof(inventoryService));
            this.owner = owner ?? throw new System.ArgumentNullException(nameof(owner));
            this.catalog = catalog;
        }

        public int InventoryPressure { get; private set; }
        public InventoryOwner Owner => owner;

        public InventoryMutationResult AddLoot(string itemDefinitionId, int quantity)
        {
            InventoryMutationResult result = inventoryService.AddItem(owner, itemDefinitionId, string.Empty, quantity);
            if (!result.Success)
            {
                InventoryPressure++;
            }

            return result;
        }

        public InventorySnapshot RequestSnapshot()
        {
            return inventoryService.RequestSnapshot(owner);
        }

        public AIInventorySummary GetSummary()
        {
            InventorySnapshot snapshot = RequestSnapshot();
            int usedSlots = snapshot.Entries.Count;
            int sellableCount = 0;
            long netWorth = 0L;

            for (int i = 0; i < snapshot.Entries.Count; i++)
            {
                InventoryEntry entry = snapshot.Entries[i];
                if (catalog != null && catalog.TryGet(entry.ItemDefinitionId, out ItemDefinitionData definition))
                {
                    int available = entry.AvailableQuantity;
                    if (IsSellable(definition))
                    {
                        sellableCount += available;
                    }

                    long unitValue = definition.BaseValue > 0 ? definition.BaseValue : definition.Rarity * 10L;
                    netWorth += unitValue * available;
                }
                else
                {
                    sellableCount += entry.AvailableQuantity;
                }
            }

            int capacity = System.Math.Max(0, owner.SlotCapacity);
            return new AIInventorySummary(
                usedSlots,
                System.Math.Max(0, capacity - usedSlots),
                capacity,
                sellableCount,
                netWorth);
        }

        public bool TryFindFirstUsableFood(out string itemDefinitionId)
        {
            itemDefinitionId = string.Empty;
            if (catalog == null)
            {
                return false;
            }

            InventorySnapshot snapshot = RequestSnapshot();
            for (int i = 0; i < snapshot.Entries.Count; i++)
            {
                InventoryEntry entry = snapshot.Entries[i];
                if (entry.AvailableQuantity <= 0)
                {
                    continue;
                }

                if (catalog.TryGet(entry.ItemDefinitionId, out ItemDefinitionData definition) &&
                    definition.UseEffect.HasEffect &&
                    definition.UseEffect.HungerDelta > 0)
                {
                    itemDefinitionId = entry.ItemDefinitionId;
                    return true;
                }
            }

            return false;
        }

        public bool TryFindFirstWithTag(string tag, out string itemDefinitionId)
        {
            itemDefinitionId = string.Empty;
            if (catalog == null || string.IsNullOrWhiteSpace(tag))
            {
                return false;
            }

            InventorySnapshot snapshot = RequestSnapshot();
            for (int i = 0; i < snapshot.Entries.Count; i++)
            {
                InventoryEntry entry = snapshot.Entries[i];
                if (entry.AvailableQuantity <= 0 || !catalog.TryGet(entry.ItemDefinitionId, out ItemDefinitionData definition))
                {
                    continue;
                }

                for (int tagIndex = 0; tagIndex < definition.Tags.Length; tagIndex++)
                {
                    if (string.Equals(definition.Tags[tagIndex], tag, System.StringComparison.Ordinal))
                    {
                        itemDefinitionId = entry.ItemDefinitionId;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsSellable(ItemDefinitionData definition)
        {
            if (definition.BaseValue > 0 || definition.Rarity > 0)
            {
                return true;
            }

            for (int i = 0; i < definition.Tags.Length; i++)
            {
                string tag = definition.Tags[i] ?? string.Empty;
                if (tag.StartsWith("resource.", System.StringComparison.Ordinal) ||
                    tag.StartsWith("material.", System.StringComparison.Ordinal) ||
                    tag.StartsWith("weapon.", System.StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }

    public readonly struct AIInventorySummary
    {
        public AIInventorySummary(int usedSlots, int freeSlots, int capacitySlots, int sellableCount, long netWorth)
        {
            UsedSlots = usedSlots;
            FreeSlots = freeSlots;
            CapacitySlots = capacitySlots;
            SellableCount = sellableCount;
            NetWorth = netWorth;
        }

        public int UsedSlots { get; }
        public int FreeSlots { get; }
        public int CapacitySlots { get; }
        public int SellableCount { get; }
        public long NetWorth { get; }
    }
}

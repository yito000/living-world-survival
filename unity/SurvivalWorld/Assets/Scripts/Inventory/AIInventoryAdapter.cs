using System;
using System.Globalization;
using SurvivalWorld.Items;

namespace SurvivalWorld.Inventory
{
    public sealed class AIInventoryAdapter
    {
        private readonly InventoryService inventoryService;
        private readonly InventoryRuntimeService runtimeService;
        private readonly InventoryOwner owner;
        private readonly IItemDefinitionCatalog catalog;
        private readonly string ownerType;
        private readonly string ownerId;
        private int generatedCommandSequence;

        public AIInventoryAdapter(InventoryService inventoryService, InventoryOwner owner)
            : this(inventoryService, owner, null)
        {
        }

        public AIInventoryAdapter(InventoryService inventoryService, InventoryOwner owner, IItemDefinitionCatalog catalog)
        {
            this.inventoryService = inventoryService ?? throw new ArgumentNullException(nameof(inventoryService));
            this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
            this.catalog = catalog;
            ownerType = owner.OwnerType;
            ownerId = owner.OwnerId;
        }

        public AIInventoryAdapter(InventoryRuntimeService runtimeService, string ownerType, string ownerId, IItemDefinitionCatalog catalog)
        {
            this.runtimeService = runtimeService ?? throw new ArgumentNullException(nameof(runtimeService));
            this.ownerType = string.IsNullOrWhiteSpace(ownerType) ? "ai" : ownerType;
            this.ownerId = string.IsNullOrWhiteSpace(ownerId) ? "ai" : ownerId;
            this.catalog = catalog;
        }

        public int InventoryPressure { get; private set; }
        public InventoryOwner Owner => owner;
        public string OwnerType => ownerType;
        public string OwnerId => ownerId;

        public InventoryMutationResult AddLoot(string itemDefinitionId, int quantity)
        {
            InventoryMutationResult result;
            if (runtimeService != null)
            {
                string commandId = "ai-loot:" + ownerId + ":" + (++generatedCommandSequence).ToString(CultureInfo.InvariantCulture);
                result = runtimeService.AddItemCommand(ownerType, ownerId, commandId, -1, itemDefinitionId, string.Empty, quantity);
            }
            else
            {
                result = inventoryService.AddItem(owner, itemDefinitionId, string.Empty, quantity);
            }

            if (!result.Success)
            {
                InventoryPressure++;
            }

            return result;
        }

        public InventorySnapshot RequestSnapshot()
        {
            return runtimeService != null
                ? runtimeService.RequestSnapshot(ownerType, ownerId)
                : inventoryService.RequestSnapshot(owner);
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

            int capacity = runtimeService != null
                ? InventoryOwner.DefaultSlotCapacity
                : Math.Max(0, owner.SlotCapacity);
            return new AIInventorySummary(
                usedSlots,
                Math.Max(0, capacity - usedSlots),
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
                    if (string.Equals(definition.Tags[tagIndex], tag, StringComparison.Ordinal))
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
                if (tag.StartsWith("resource.", StringComparison.Ordinal) ||
                    tag.StartsWith("material.", StringComparison.Ordinal) ||
                    tag.StartsWith("weapon.", StringComparison.Ordinal))
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
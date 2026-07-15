using System;
using System.Collections.Generic;
using SurvivalWorld.Inventory;
using SurvivalWorld.Items;
using SurvivalWorld.Shared.MasterData;

namespace SurvivalWorld.Server.Inventory
{
    public sealed class M3InventoryService
    {
        private readonly IItemDefinitionCatalog catalog;

        public M3InventoryService(IItemDefinitionCatalog catalog)
        {
            this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        }

        public bool HasTag(string itemDefinitionId, string tag)
        {
            if (!catalog.TryGet(itemDefinitionId, out ItemDefinitionData definition) || definition.Tags == null)
            {
                return false;
            }

            for (int i = 0; i < definition.Tags.Length; i++)
            {
                if (string.Equals(definition.Tags[i], tag ?? string.Empty, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        public int CountAvailable(InventoryOwner owner, string itemDefinitionId)
        {
            if (owner == null)
            {
                return 0;
            }

            int total = 0;
            for (int i = 0; i < owner.Entries.Count; i++)
            {
                InventoryEntry entry = owner.Entries[i];
                if (string.Equals(entry.ItemDefinitionId, itemDefinitionId ?? string.Empty, StringComparison.Ordinal))
                {
                    total += entry.AvailableQuantity;
                }
            }

            return total;
        }

        public int CountReserved(InventoryOwner owner, string itemDefinitionId)
        {
            if (owner == null)
            {
                return 0;
            }

            int total = 0;
            for (int i = 0; i < owner.Entries.Count; i++)
            {
                InventoryEntry entry = owner.Entries[i];
                if (string.Equals(entry.ItemDefinitionId, itemDefinitionId ?? string.Empty, StringComparison.Ordinal))
                {
                    total += entry.Reserved;
                }
            }

            return total;
        }

        public bool CanGrant(InventoryOwner owner, string itemDefinitionId, string itemInstanceId, int quantity, out string reason)
        {
            reason = string.Empty;
            if (owner == null)
            {
                reason = "Owner is required.";
                return false;
            }

            if (!catalog.TryGet(itemDefinitionId, out ItemDefinitionData definition))
            {
                reason = "Unknown item definition: " + itemDefinitionId;
                return false;
            }

            if (quantity <= 0)
            {
                reason = "Quantity must be positive.";
                return false;
            }

            if (WouldExceedWeight(owner, definition, quantity))
            {
                reason = "Inventory weight capacity exceeded.";
                return false;
            }

            bool stackableInstance = string.IsNullOrWhiteSpace(itemInstanceId) && definition.StackLimit > 1;
            if (!HasSlotCapacityForAdd(owner, definition, itemDefinitionId, itemInstanceId, quantity, stackableInstance))
            {
                reason = "Inventory slot capacity exceeded.";
                return false;
            }

            return true;
        }

        public M3InventoryResult Grant(InventoryOwner owner, ItemStack stack)
        {
            if (!CanGrant(owner, stack.ItemDefinitionId, stack.ItemInstanceId, stack.Quantity, out string reason))
            {
                return M3InventoryResult.Rejected(reason);
            }

            catalog.TryGet(stack.ItemDefinitionId, out ItemDefinitionData definition);
            int remaining = stack.Quantity;
            bool stackableInstance = string.IsNullOrWhiteSpace(stack.ItemInstanceId) && definition.StackLimit > 1;
            if (stackableInstance)
            {
                for (int i = 0; i < owner.Entries.Count && remaining > 0; i++)
                {
                    InventoryEntry entry = owner.Entries[i];
                    if (!IsSameStack(entry, stack.ItemDefinitionId, string.Empty))
                    {
                        continue;
                    }

                    int moved = Math.Min(remaining, Math.Max(0, definition.StackLimit - entry.Quantity));
                    entry.Quantity += moved;
                    owner.Entries[i] = entry;
                    remaining -= moved;
                }
            }

            while (remaining > 0)
            {
                int slot = FindFirstFreeSlot(owner);
                if (slot < 0)
                {
                    return M3InventoryResult.Rejected("Inventory slot capacity exceeded.");
                }

                int stackQuantity = stackableInstance ? Math.Min(remaining, definition.StackLimit) : 1;
                owner.Entries.Add(new InventoryEntry(slot, stack.ItemDefinitionId, stack.ItemInstanceId, stackQuantity, 0));
                remaining -= stackQuantity;
            }

            owner.Version++;
            return M3InventoryResult.Ok();
        }

        public M3InventoryResult Reserve(InventoryOwner owner, IReadOnlyList<ItemStack> items)
        {
            if (owner == null)
            {
                throw new ArgumentNullException(nameof(owner));
            }

            if (items == null || items.Count == 0)
            {
                return M3InventoryResult.Ok();
            }

            for (int i = 0; i < items.Count; i++)
            {
                if (CountAvailable(owner, items[i].ItemDefinitionId) < items[i].Quantity)
                {
                    return M3InventoryResult.Rejected("Insufficient available quantity for " + items[i].ItemDefinitionId);
                }
            }

            for (int i = 0; i < items.Count; i++)
            {
                ReserveAvailable(owner, items[i].ItemDefinitionId, items[i].Quantity);
            }

            owner.Version++;
            return M3InventoryResult.Ok();
        }

        public M3InventoryResult ReleaseReserved(InventoryOwner owner, IReadOnlyList<ItemStack> items)
        {
            if (owner == null)
            {
                throw new ArgumentNullException(nameof(owner));
            }

            if (items == null || items.Count == 0)
            {
                return M3InventoryResult.Ok();
            }

            for (int i = 0; i < items.Count; i++)
            {
                if (CountReserved(owner, items[i].ItemDefinitionId) < items[i].Quantity)
                {
                    return M3InventoryResult.Rejected("Reserved quantity missing for " + items[i].ItemDefinitionId);
                }
            }

            for (int i = 0; i < items.Count; i++)
            {
                ReleaseReserved(owner, items[i].ItemDefinitionId, items[i].Quantity);
            }

            owner.Version++;
            return M3InventoryResult.Ok();
        }

        public M3InventoryResult ConsumeReserved(InventoryOwner owner, IReadOnlyList<ItemStack> items)
        {
            if (owner == null)
            {
                throw new ArgumentNullException(nameof(owner));
            }

            if (items == null || items.Count == 0)
            {
                return M3InventoryResult.Ok();
            }

            for (int i = 0; i < items.Count; i++)
            {
                if (CountReserved(owner, items[i].ItemDefinitionId) < items[i].Quantity)
                {
                    return M3InventoryResult.Rejected("Reserved quantity missing for " + items[i].ItemDefinitionId);
                }
            }

            for (int i = 0; i < items.Count; i++)
            {
                ConsumeReserved(owner, items[i].ItemDefinitionId, items[i].Quantity);
            }

            owner.Version++;
            return M3InventoryResult.Ok();
        }

        public M3InventoryResult ConsumeAvailable(InventoryOwner owner, string itemDefinitionId, int quantity)
        {
            if (owner == null)
            {
                throw new ArgumentNullException(nameof(owner));
            }

            if (quantity <= 0)
            {
                return M3InventoryResult.Rejected("Quantity must be positive.");
            }

            if (CountAvailable(owner, itemDefinitionId) < quantity)
            {
                return M3InventoryResult.Rejected("Insufficient available quantity for " + itemDefinitionId);
            }

            ConsumeAvailableInternal(owner, itemDefinitionId, quantity);
            owner.Version++;
            return M3InventoryResult.Ok();
        }

        public bool CanGrantAll(InventoryOwner owner, IReadOnlyList<ItemStack> stacks, out string reason)
        {
            reason = string.Empty;
            if (stacks == null)
            {
                return true;
            }

            var simulated = new InventoryOwner(owner.OwnerType, owner.OwnerId, owner.SlotCapacity, owner.WeightCapacity, owner.Version);
            for (int i = 0; i < owner.Entries.Count; i++)
            {
                simulated.Entries.Add(owner.Entries[i]);
            }

            for (int i = 0; i < stacks.Count; i++)
            {
                if (!CanGrant(simulated, stacks[i].ItemDefinitionId, stacks[i].ItemInstanceId, stacks[i].Quantity, out reason))
                {
                    return false;
                }

                Grant(simulated, stacks[i]);
            }

            return true;
        }

        private bool WouldExceedWeight(InventoryOwner owner, ItemDefinitionData definition, int quantity)
        {
            float currentWeight = 0f;
            for (int i = 0; i < owner.Entries.Count; i++)
            {
                InventoryEntry entry = owner.Entries[i];
                if (catalog.TryGet(entry.ItemDefinitionId, out ItemDefinitionData entryDefinition))
                {
                    currentWeight += entryDefinition.Weight * entry.Quantity;
                }
            }

            return currentWeight + (definition.Weight * quantity) > owner.WeightCapacity + 0.0001f;
        }

        private static bool HasSlotCapacityForAdd(InventoryOwner owner, ItemDefinitionData definition, string itemDefinitionId, string itemInstanceId, int quantity, bool stackableInstance)
        {
            int remaining = quantity;
            if (stackableInstance)
            {
                for (int i = 0; i < owner.Entries.Count && remaining > 0; i++)
                {
                    InventoryEntry entry = owner.Entries[i];
                    if (!IsSameStack(entry, itemDefinitionId, string.Empty))
                    {
                        continue;
                    }

                    remaining -= Math.Max(0, definition.StackLimit - entry.Quantity);
                }
            }

            int freeSlots = Math.Max(0, owner.SlotCapacity - owner.Entries.Count);
            int requiredSlots = stackableInstance
                ? (int)Math.Ceiling(remaining / (double)Math.Max(1, definition.StackLimit))
                : remaining;
            return requiredSlots <= freeSlots;
        }

        private static void ReserveAvailable(InventoryOwner owner, string itemDefinitionId, int quantity)
        {
            int remaining = quantity;
            for (int i = 0; i < owner.Entries.Count && remaining > 0; i++)
            {
                InventoryEntry entry = owner.Entries[i];
                if (!string.Equals(entry.ItemDefinitionId, itemDefinitionId ?? string.Empty, StringComparison.Ordinal))
                {
                    continue;
                }

                int moved = Math.Min(remaining, entry.AvailableQuantity);
                entry.Reserved += moved;
                owner.Entries[i] = entry;
                remaining -= moved;
            }
        }

        private static void ReleaseReserved(InventoryOwner owner, string itemDefinitionId, int quantity)
        {
            int remaining = quantity;
            for (int i = 0; i < owner.Entries.Count && remaining > 0; i++)
            {
                InventoryEntry entry = owner.Entries[i];
                if (!string.Equals(entry.ItemDefinitionId, itemDefinitionId ?? string.Empty, StringComparison.Ordinal))
                {
                    continue;
                }

                int moved = Math.Min(remaining, entry.Reserved);
                entry.Reserved -= moved;
                owner.Entries[i] = entry;
                remaining -= moved;
            }
        }

        private static void ConsumeReserved(InventoryOwner owner, string itemDefinitionId, int quantity)
        {
            int remaining = quantity;
            for (int i = owner.Entries.Count - 1; i >= 0 && remaining > 0; i--)
            {
                InventoryEntry entry = owner.Entries[i];
                if (!string.Equals(entry.ItemDefinitionId, itemDefinitionId ?? string.Empty, StringComparison.Ordinal))
                {
                    continue;
                }

                int moved = Math.Min(remaining, entry.Reserved);
                entry.Reserved -= moved;
                entry.Quantity -= moved;
                remaining -= moved;
                if (entry.Quantity <= 0)
                {
                    owner.Entries.RemoveAt(i);
                }
                else
                {
                    owner.Entries[i] = entry;
                }
            }
        }

        private static void ConsumeAvailableInternal(InventoryOwner owner, string itemDefinitionId, int quantity)
        {
            int remaining = quantity;
            for (int i = owner.Entries.Count - 1; i >= 0 && remaining > 0; i--)
            {
                InventoryEntry entry = owner.Entries[i];
                if (!string.Equals(entry.ItemDefinitionId, itemDefinitionId ?? string.Empty, StringComparison.Ordinal))
                {
                    continue;
                }

                int moved = Math.Min(remaining, entry.AvailableQuantity);
                entry.Quantity -= moved;
                remaining -= moved;
                if (entry.Quantity <= 0)
                {
                    owner.Entries.RemoveAt(i);
                }
                else
                {
                    owner.Entries[i] = entry;
                }
            }
        }

        private static bool IsSameStack(InventoryEntry entry, string itemDefinitionId, string itemInstanceId)
        {
            return string.Equals(entry.ItemDefinitionId, itemDefinitionId ?? string.Empty, StringComparison.Ordinal) &&
                   string.Equals(entry.ItemInstanceId, itemInstanceId ?? string.Empty, StringComparison.Ordinal);
        }

        private static int FindFirstFreeSlot(InventoryOwner owner)
        {
            for (int slot = 0; slot < owner.SlotCapacity; slot++)
            {
                bool occupied = false;
                for (int i = 0; i < owner.Entries.Count; i++)
                {
                    if (owner.Entries[i].SlotIndex == slot)
                    {
                        occupied = true;
                        break;
                    }
                }

                if (!occupied)
                {
                    return slot;
                }
            }

            return -1;
        }
    }

    public readonly struct M3InventoryResult
    {
        private M3InventoryResult(bool success, string error)
        {
            Success = success;
            Error = error ?? string.Empty;
        }

        public bool Success { get; }
        public string Error { get; }

        public static M3InventoryResult Ok()
        {
            return new M3InventoryResult(true, string.Empty);
        }

        public static M3InventoryResult Rejected(string error)
        {
            return new M3InventoryResult(false, error);
        }
    }
}

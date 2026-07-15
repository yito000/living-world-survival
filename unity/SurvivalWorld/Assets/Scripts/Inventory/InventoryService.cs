using System;
using System.Collections.Generic;
using System.Globalization;
using Google.Protobuf;
using Survival.V1;
using SurvivalWorld.Items;
using SurvivalWorld.World;

namespace SurvivalWorld.Inventory
{
    public sealed class InventoryService
    {
        private readonly IItemDefinitionCatalog catalog;
        private readonly IInventoryEventSink eventSink;
        private readonly string worldId;
        private readonly Dictionary<string, Dictionary<string, InventoryMutationResult>> processedCommandsByOwner = new Dictionary<string, Dictionary<string, InventoryMutationResult>>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> localSequenceByOwner = new Dictionary<string, long>(StringComparer.Ordinal);

        public InventoryService(IItemDefinitionCatalog catalog)
            : this(catalog, NullInventoryEventSink.Instance, "runtime")
        {
        }

        public InventoryService(IItemDefinitionCatalog catalog, IInventoryEventSink eventSink, string worldId)
        {
            this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            this.eventSink = eventSink ?? NullInventoryEventSink.Instance;
            this.worldId = string.IsNullOrWhiteSpace(worldId) ? "runtime" : worldId;
        }

        public InventorySnapshot RequestSnapshot(InventoryOwner owner)
        {
            return InventorySnapshot.FromOwner(owner);
        }

        public InventoryMutationResult AddItem(InventoryOwner owner, string itemDefinitionId, string itemInstanceId, int quantity)
        {
            return AddItemInternal(owner, itemDefinitionId, itemInstanceId, quantity, "internal:add", "inventory.add");
        }

        public InventoryMutationResult AddItemCommand(InventoryOwner owner, string commandId, long expectedVersion, string itemDefinitionId, string itemInstanceId, int quantity)
        {
            if (owner == null)
            {
                throw new ArgumentNullException(nameof(owner));
            }

            if (string.IsNullOrWhiteSpace(commandId))
            {
                return InventoryMutationResult.Rejected(RequestSnapshot(owner), "Command id is required.");
            }

            Dictionary<string, InventoryMutationResult> ownerCommands = GetOwnerCommandResults(owner.OwnerId);
            if (ownerCommands.TryGetValue(commandId, out InventoryMutationResult previous))
            {
                return InventoryMutationResult.Duplicate(previous.Snapshot);
            }

            if (expectedVersion >= 0 && expectedVersion != owner.Version)
            {
                return InventoryMutationResult.Conflict(RequestSnapshot(owner), "Inventory version conflict.");
            }

            InventoryMutationResult result = AddItemInternal(owner, itemDefinitionId, itemInstanceId, quantity, commandId, "inventory.item_added");
            if (result.Success && result.Status == InventoryMutationStatus.Ok)
            {
                ownerCommands[commandId] = result;
            }

            return result;
        }

        private InventoryMutationResult AddItemInternal(InventoryOwner owner, string itemDefinitionId, string itemInstanceId, int quantity, string commandId, string eventType)
        {
            if (owner == null)
            {
                throw new ArgumentNullException(nameof(owner));
            }

            if (!catalog.TryGet(itemDefinitionId, out ItemDefinitionData definition))
            {
                return InventoryMutationResult.Rejected(RequestSnapshot(owner), "Unknown item definition: " + itemDefinitionId);
            }

            if (quantity <= 0)
            {
                return InventoryMutationResult.Rejected(RequestSnapshot(owner), "Quantity must be positive.");
            }

            if (WouldExceedWeight(owner, definition, quantity))
            {
                return InventoryMutationResult.Rejected(RequestSnapshot(owner), "Inventory weight capacity exceeded.");
            }

            int remaining = quantity;
            bool stackableInstance = string.IsNullOrWhiteSpace(itemInstanceId) && definition.StackLimit > 1;
            if (!HasSlotCapacityForAdd(owner, definition, itemDefinitionId, itemInstanceId, quantity, stackableInstance))
            {
                return InventoryMutationResult.Rejected(RequestSnapshot(owner), "Inventory slot capacity exceeded.");
            }

            if (stackableInstance)
            {
                for (int i = 0; i < owner.Entries.Count && remaining > 0; i++)
                {
                    InventoryEntry entry = owner.Entries[i];
                    if (!IsSameStack(entry, itemDefinitionId, string.Empty))
                    {
                        continue;
                    }

                    int availableSpace = Math.Max(0, definition.StackLimit - entry.Quantity);
                    int moved = Math.Min(remaining, availableSpace);
                    if (moved <= 0)
                    {
                        continue;
                    }

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
                    return InventoryMutationResult.Rejected(RequestSnapshot(owner), "Inventory slot capacity exceeded.");
                }

                int stackQuantity = stackableInstance ? Math.Min(remaining, definition.StackLimit) : 1;
                owner.Entries.Add(new InventoryEntry(slot, itemDefinitionId, itemInstanceId, stackQuantity, 0));
                remaining -= stackQuantity;
            }

            owner.Version++;
            DomainEvent domainEvent = CreateEvent(owner, eventType, commandId, quantity);
            eventSink.Enqueue(domainEvent);
            return InventoryMutationResult.Ok(RequestSnapshot(owner), domainEvent);
        }

        public InventoryMutationResult ApplyCommand(InventoryOwner owner, InventoryCommand command)
        {
            if (owner == null)
            {
                throw new ArgumentNullException(nameof(owner));
            }

            if (command == null)
            {
                return InventoryMutationResult.Rejected(RequestSnapshot(owner), "Command is null.");
            }

            if (string.IsNullOrWhiteSpace(command.CommandId))
            {
                return InventoryMutationResult.Rejected(RequestSnapshot(owner), "Command id is required.");
            }

            Dictionary<string, InventoryMutationResult> ownerCommands = GetOwnerCommandResults(owner.OwnerId);
            if (ownerCommands.TryGetValue(command.CommandId, out InventoryMutationResult previous))
            {
                return InventoryMutationResult.Duplicate(previous.Snapshot);
            }

            if (command.ExpectedVersion != owner.Version)
            {
                return InventoryMutationResult.Conflict(RequestSnapshot(owner), "Inventory version conflict.");
            }

            InventoryMutationResult result;
            switch (command.Operation)
            {
                case InventoryOperation.Move:
                    result = ApplyMove(owner, command);
                    break;
                case InventoryOperation.Split:
                    result = ApplySplit(owner, command);
                    break;
                case InventoryOperation.Merge:
                    result = ApplyMerge(owner, command);
                    break;
                case InventoryOperation.Drop:
                    result = ApplyDrop(owner, command);
                    break;
                case InventoryOperation.Use:
                    result = ApplyUse(owner, command);
                    break;
                default:
                    result = InventoryMutationResult.Rejected(RequestSnapshot(owner), "Unsupported inventory operation.");
                    break;
            }

            if (result.Success && result.Status == InventoryMutationStatus.Ok)
            {
                ownerCommands[command.CommandId] = result;
                if (result.DomainEvent != null)
                {
                    eventSink.Enqueue(result.DomainEvent);
                }
            }

            return result;
        }

        private InventoryMutationResult ApplyMove(InventoryOwner owner, InventoryCommand command)
        {
            int sourceIndex = FindEntryIndex(owner, command.ItemRef);
            if (sourceIndex < 0)
            {
                return InventoryMutationResult.Rejected(RequestSnapshot(owner), "Source item not found.");
            }

            if (!TryParseTargetSlot(command.TargetRef, out int targetSlot))
            {
                return InventoryMutationResult.Rejected(RequestSnapshot(owner), "Move target slot is required.");
            }

            if (targetSlot < 0 || targetSlot >= owner.SlotCapacity)
            {
                return InventoryMutationResult.Rejected(RequestSnapshot(owner), "Move target slot is outside capacity.");
            }

            int occupiedIndex = owner.Entries.FindIndex(entry => entry.SlotIndex == targetSlot);
            if (occupiedIndex >= 0 && occupiedIndex != sourceIndex)
            {
                return InventoryMutationResult.Rejected(RequestSnapshot(owner), "Move target slot is occupied.");
            }

            InventoryEntry source = owner.Entries[sourceIndex];
            source.SlotIndex = targetSlot;
            owner.Entries[sourceIndex] = source;
            return CompleteCommand(owner, command, "inventory.move");
        }

        private InventoryMutationResult ApplySplit(InventoryOwner owner, InventoryCommand command)
        {
            int sourceIndex = FindEntryIndex(owner, command.ItemRef);
            if (sourceIndex < 0)
            {
                return InventoryMutationResult.Rejected(RequestSnapshot(owner), "Source item not found.");
            }

            InventoryEntry source = owner.Entries[sourceIndex];
            if (!catalog.TryGet(source.ItemDefinitionId, out ItemDefinitionData definition) || definition.StackLimit <= 1)
            {
                return InventoryMutationResult.Rejected(RequestSnapshot(owner), "Source item is not stackable.");
            }

            int quantity = command.Quantity;
            if (quantity <= 0 || quantity >= source.AvailableQuantity)
            {
                return InventoryMutationResult.Rejected(RequestSnapshot(owner), "Split quantity must be less than available quantity.");
            }

            int targetSlot = FindFirstFreeSlot(owner);
            if (TryParseTargetSlot(command.TargetRef, out int requestedSlot))
            {
                targetSlot = requestedSlot;
            }

            if (targetSlot < 0 || targetSlot >= owner.SlotCapacity || owner.Entries.Exists(entry => entry.SlotIndex == targetSlot))
            {
                return InventoryMutationResult.Rejected(RequestSnapshot(owner), "No empty target slot for split.");
            }

            source.Quantity -= quantity;
            owner.Entries[sourceIndex] = source;
            owner.Entries.Add(new InventoryEntry(targetSlot, source.ItemDefinitionId, source.ItemInstanceId, quantity, 0));
            return CompleteCommand(owner, command, "inventory.split");
        }

        private InventoryMutationResult ApplyMerge(InventoryOwner owner, InventoryCommand command)
        {
            int sourceIndex = FindEntryIndex(owner, command.ItemRef);
            int targetIndex = FindEntryIndex(owner, command.TargetRef);
            if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex)
            {
                return InventoryMutationResult.Rejected(RequestSnapshot(owner), "Merge source and target are required.");
            }

            InventoryEntry source = owner.Entries[sourceIndex];
            InventoryEntry target = owner.Entries[targetIndex];
            if (!IsSameStack(source, target.ItemDefinitionId, target.ItemInstanceId) ||
                !catalog.TryGet(target.ItemDefinitionId, out ItemDefinitionData definition))
            {
                return InventoryMutationResult.Rejected(RequestSnapshot(owner), "Merge target is incompatible.");
            }

            int requested = command.Quantity <= 0 ? source.AvailableQuantity : Math.Min(command.Quantity, source.AvailableQuantity);
            int moved = Math.Min(requested, Math.Max(0, definition.StackLimit - target.Quantity));
            if (moved <= 0)
            {
                return InventoryMutationResult.Rejected(RequestSnapshot(owner), "Merge target has no stack space.");
            }

            source.Quantity -= moved;
            target.Quantity += moved;
            owner.Entries[targetIndex] = target;
            if (source.Quantity <= 0)
            {
                owner.Entries.RemoveAt(sourceIndex);
            }
            else
            {
                owner.Entries[sourceIndex] = source;
            }

            return CompleteCommand(owner, command, "inventory.merge");
        }

        private InventoryMutationResult ApplyDrop(InventoryOwner owner, InventoryCommand command)
        {
            int sourceIndex = FindEntryIndex(owner, command.ItemRef);
            if (sourceIndex < 0)
            {
                return InventoryMutationResult.Rejected(RequestSnapshot(owner), "Source item not found.");
            }

            InventoryEntry source = owner.Entries[sourceIndex];
            int quantity = command.Quantity <= 0 ? source.AvailableQuantity : command.Quantity;
            if (quantity <= 0 || quantity > source.AvailableQuantity)
            {
                return InventoryMutationResult.Rejected(RequestSnapshot(owner), "Drop quantity exceeds available quantity.");
            }

            source.Quantity -= quantity;
            if (source.Quantity <= 0)
            {
                owner.Entries.RemoveAt(sourceIndex);
            }
            else
            {
                owner.Entries[sourceIndex] = source;
            }

            return CompleteCommand(owner, command, "inventory.drop");
        }

        private InventoryMutationResult ApplyUse(InventoryOwner owner, InventoryCommand command)
        {
            int sourceIndex = FindEntryIndex(owner, command.ItemRef);
            if (sourceIndex < 0)
            {
                return InventoryMutationResult.Rejected(RequestSnapshot(owner), "Source item not found.");
            }

            InventoryEntry source = owner.Entries[sourceIndex];
            if (!catalog.TryGet(source.ItemDefinitionId, out ItemDefinitionData definition) || !definition.UseEffect.HasEffect)
            {
                return InventoryMutationResult.Rejected(RequestSnapshot(owner), "Item has no use effect.");
            }

            int quantity = command.Quantity <= 0 ? 1 : command.Quantity;
            if (quantity > source.AvailableQuantity)
            {
                return InventoryMutationResult.Rejected(RequestSnapshot(owner), "Use quantity exceeds available quantity.");
            }

            source.Quantity -= quantity;
            if (source.Quantity <= 0)
            {
                owner.Entries.RemoveAt(sourceIndex);
            }
            else
            {
                owner.Entries[sourceIndex] = source;
            }

            return CompleteCommand(owner, command, "inventory.use");
        }

        private InventoryMutationResult CompleteCommand(InventoryOwner owner, InventoryCommand command, string eventType)
        {
            owner.Version++;
            DomainEvent domainEvent = CreateEvent(owner, eventType, command.CommandId, command.Quantity);
            return InventoryMutationResult.Ok(RequestSnapshot(owner), domainEvent);
        }

        private Dictionary<string, InventoryMutationResult> GetOwnerCommandResults(string ownerId)
        {
            if (!processedCommandsByOwner.TryGetValue(ownerId, out Dictionary<string, InventoryMutationResult> ownerCommands))
            {
                ownerCommands = new Dictionary<string, InventoryMutationResult>(StringComparer.Ordinal);
                processedCommandsByOwner[ownerId] = ownerCommands;
            }

            return ownerCommands;
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

        private static bool IsSameStack(InventoryEntry entry, string itemDefinitionId, string itemInstanceId)
        {
            return string.Equals(entry.ItemDefinitionId, itemDefinitionId ?? string.Empty, StringComparison.Ordinal) &&
                   string.Equals(entry.ItemInstanceId, itemInstanceId ?? string.Empty, StringComparison.Ordinal);
        }

        private static int FindEntryIndex(InventoryOwner owner, ItemRef itemRef)
        {
            if (itemRef == null)
            {
                return -1;
            }

            if (!string.IsNullOrWhiteSpace(itemRef.ItemInstanceId))
            {
                int instanceIndex = owner.Entries.FindIndex(entry => string.Equals(entry.ItemInstanceId, itemRef.ItemInstanceId, StringComparison.Ordinal));
                if (instanceIndex >= 0)
                {
                    return instanceIndex;
                }
            }

            if (!string.IsNullOrWhiteSpace(itemRef.ItemDefinitionId))
            {
                return owner.Entries.FindIndex(entry => string.Equals(entry.ItemDefinitionId, itemRef.ItemDefinitionId, StringComparison.Ordinal));
            }

            return -1;
        }

        private static bool TryParseTargetSlot(ItemRef targetRef, out int slot)
        {
            slot = -1;
            if (targetRef == null)
            {
                return false;
            }

            return TryParseSlotText(targetRef.ItemInstanceId, out slot) ||
                   TryParseSlotText(targetRef.ItemDefinitionId, out slot);
        }

        private static bool TryParseSlotText(string text, out int slot)
        {
            slot = -1;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string normalized = text.Trim();
            if (normalized.StartsWith("slot:", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(5);
            }
            else if (normalized.StartsWith("slot_", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(5);
            }

            return int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out slot);
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

        private DomainEvent CreateEvent(InventoryOwner owner, string type, string commandId, int quantity)
        {
            long sequence = NextLocalSequence(owner.OwnerId);
            string payload = string.Format(
                CultureInfo.InvariantCulture,
                "{{\"owner_type\":\"{0}\",\"owner_id\":\"{1}\",\"command_id\":\"{2}\",\"quantity\":{3},\"version\":{4}}}",
                Escape(owner.OwnerType),
                Escape(owner.OwnerId),
                Escape(commandId),
                quantity,
                owner.Version);

            return new DomainEvent
            {
                EventId = DomainEventId.NewUlid(),
                WorldId = worldId,
                AggregateId = owner.OwnerId,
                LocalSequence = sequence,
                Type = type,
                Payload = ByteString.CopyFromUtf8(payload),
                OccurredAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }

        private long NextLocalSequence(string ownerId)
        {
            if (!localSequenceByOwner.TryGetValue(ownerId, out long current))
            {
                current = 0;
            }

            current++;
            localSequenceByOwner[ownerId] = current;
            return current;
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}


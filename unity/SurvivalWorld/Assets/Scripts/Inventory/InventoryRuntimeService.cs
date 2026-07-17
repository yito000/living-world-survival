using System;
using System.Collections.Generic;
using Survival.V1;
using SurvivalWorld.Items;

namespace SurvivalWorld.Inventory
{
    public sealed class InventoryRuntimeService
    {
        private readonly InventoryService inventoryService;
        private readonly Dictionary<string, InventoryOwner> owners = new Dictionary<string, InventoryOwner>(StringComparer.Ordinal);

        public InventoryRuntimeService(IItemDefinitionCatalog catalog, IInventoryEventSink eventSink, string worldId)
        {
            inventoryService = new InventoryService(catalog, eventSink, worldId);
        }

        public InventoryMutationResult AddItemCommand(string ownerType, string ownerId, string commandId, long expectedVersion, string itemDefinitionId, string itemInstanceId, int quantity)
        {
            InventoryOwner owner = GetOrCreateOwner(ownerType, ownerId);
            long resolvedVersion = expectedVersion < 0 ? owner.Version : expectedVersion;
            return inventoryService.AddItemCommand(owner, commandId, resolvedVersion, itemDefinitionId, itemInstanceId, quantity);
        }

        public InventoryMutationResult ApplyCommand(string ownerType, string ownerId, InventoryCommand command)
        {
            InventoryOwner owner = GetOrCreateOwner(ownerType, ownerId);
            if (command != null && command.ExpectedVersion < 0)
            {
                command = command.Clone();
                command.ExpectedVersion = owner.Version;
            }

            return inventoryService.ApplyCommand(owner, command);
        }

        public InventoryMutationResult ApplyApiGrantedItems(string ownerType, string ownerId, string commandId, long expectedVersion, IEnumerable<ItemRef> grantedItems, IEnumerable<string> itemInstanceIds)
        {
            InventoryOwner owner = GetOrCreateOwner(ownerType, ownerId);
            long resolvedVersion = expectedVersion < 0 ? owner.Version : expectedVersion;
            return inventoryService.ApplyApiGrantedItems(owner, commandId, resolvedVersion, grantedItems, itemInstanceIds);
        }

        public InventorySnapshot ApplySnapshot(string ownerType, string ownerId, long version, IEnumerable<InventoryEntry> entries)
        {
            InventoryOwner owner = GetOrCreateOwner(ownerType, ownerId);
            return inventoryService.ApplySnapshot(owner, version, entries);
        }
        public InventorySnapshot RequestSnapshot(string ownerType, string ownerId)
        {
            return inventoryService.RequestSnapshot(GetOrCreateOwner(ownerType, ownerId));
        }

        private InventoryOwner GetOrCreateOwner(string ownerType, string ownerId)
        {
            string normalizedOwnerType = string.IsNullOrWhiteSpace(ownerType) ? "player" : ownerType;
            string normalizedOwnerId = string.IsNullOrWhiteSpace(ownerId) ? "unknown" : ownerId;
            string key = normalizedOwnerType + ":" + normalizedOwnerId;
            if (!owners.TryGetValue(key, out InventoryOwner owner))
            {
                owner = new InventoryOwner(normalizedOwnerType, normalizedOwnerId);
                owners[key] = owner;
            }

            return owner;
        }
    }
}
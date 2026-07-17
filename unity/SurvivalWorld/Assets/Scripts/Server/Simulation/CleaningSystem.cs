using System;
using System.Collections.Generic;
using UnityEngine;
using SurvivalWorld.Inventory;
using SurvivalWorld.Server.Inventory;
using SurvivalWorld.Shared;
using SurvivalWorld.Shared.Events;
using SurvivalWorld.Shared.MasterData;
using SurvivalWorld.World;

namespace SurvivalWorld.Server.Simulation
{
    public sealed class CleaningSystem
    {
        private readonly M3InventoryService inventory;
        private readonly DomainEventFactory eventFactory;
        private readonly IInventoryEventSink eventSink;
        private readonly Dictionary<string, WorldItemState> worldItems = new Dictionary<string, WorldItemState>(StringComparer.Ordinal);

        public CleaningSystem(M3InventoryService inventory, DomainEventFactory eventFactory, IInventoryEventSink eventSink)
        {
            this.inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
            this.eventFactory = eventFactory ?? throw new ArgumentNullException(nameof(eventFactory));
            this.eventSink = eventSink ?? NullInventoryEventSink.Instance;
        }

        public IReadOnlyDictionary<string, WorldItemState> WorldItems => worldItems;

        public void RegisterWorldItem(WorldItemState worldItem)
        {
            if (worldItem != null && !string.IsNullOrWhiteSpace(worldItem.WorldItemId))
            {
                worldItems[worldItem.WorldItemId] = worldItem;
            }
        }

        public DiscardResult Discard(InventoryOwner owner, string actorId, string itemDefinitionId, int quantity, Vector3 position, long unixTimeMs)
        {
            M3InventoryResult consumed = inventory.ConsumeAvailable(owner, itemDefinitionId, quantity);
            if (!consumed.Success)
            {
                return DiscardResult.Rejected(consumed.Error);
            }

            string worldItemId = DomainEventId.NewUlid(unixTimeMs);
            var worldItem = new WorldItemState(worldItemId, itemDefinitionId, quantity, position, inventory.HasTag(itemDefinitionId, "waste.food"));
            worldItems[worldItemId] = worldItem;
            string tags = worldItem.IsWaste ? JsonPayload.StringArray(new[] { "waste.food" }) : "[]";
            eventSink.Enqueue(eventFactory.Create(
                actorId,
                DomainEventTypes.ItemDiscarded,
                JsonPayload.Object(
                    JsonPayload.Field("actor_id", actorId),
                    JsonPayload.Field("world_item_id", worldItemId),
                    JsonPayload.Field("item_definition_id", itemDefinitionId),
                    JsonPayload.OptionalField("item_instance_id", string.Empty),
                    JsonPayload.Field("quantity", quantity),
                    JsonPayload.Raw("position", JsonPayload.Vector3(position.x, position.y, position.z)),
                    JsonPayload.Raw("tags", tags)),
                string.Empty,
                unixTimeMs));
            return DiscardResult.Accepted(worldItemId);
        }

        public M3CommandResult Clean(string worldItemId, long unixTimeMs)
        {
            if (!worldItems.TryGetValue(worldItemId ?? string.Empty, out WorldItemState worldItem))
            {
                return M3CommandResult.Rejected("World item not found.");
            }

            if (!worldItem.IsWaste)
            {
                return M3CommandResult.Rejected("World item is not cleanable waste.");
            }

            if (worldItem.Cleaned)
            {
                return M3CommandResult.Rejected("World item already cleaned.");
            }

            worldItem.Cleaned = true;
            eventSink.Enqueue(eventFactory.Create(
                worldItem.WorldItemId,
                DomainEventTypes.CleaningCompleted,
                JsonPayload.Object(
                    JsonPayload.Field("world_item_id", worldItem.WorldItemId),
                    JsonPayload.Field("disposed_item_definition_id", worldItem.ItemDefinitionId),
                    JsonPayload.Field("reward_amount", SurvivalTuning.FoodWasteCleaningReward)),
                string.Empty,
                unixTimeMs));
            return M3CommandResult.Ok();
        }
    }

    public sealed class WorldItemState
    {
        public WorldItemState(string worldItemId, string itemDefinitionId, int quantity, Vector3 position, bool isWaste)
        {
            WorldItemId = worldItemId ?? string.Empty;
            ItemDefinitionId = itemDefinitionId ?? string.Empty;
            Quantity = quantity;
            Position = position;
            IsWaste = isWaste;
        }

        public string WorldItemId { get; }
        public string ItemDefinitionId { get; }
        public int Quantity { get; }
        public Vector3 Position { get; }
        public bool IsWaste { get; }
        public bool Cleaned { get; set; }
    }

    public readonly struct DiscardResult
    {
        private DiscardResult(bool success, string worldItemId, string error)
        {
            Success = success;
            WorldItemId = worldItemId ?? string.Empty;
            Error = error ?? string.Empty;
        }

        public bool Success { get; }
        public string WorldItemId { get; }
        public string Error { get; }

        public static DiscardResult Accepted(string worldItemId)
        {
            return new DiscardResult(true, worldItemId, string.Empty);
        }

        public static DiscardResult Rejected(string error)
        {
            return new DiscardResult(false, string.Empty, error);
        }
    }
}

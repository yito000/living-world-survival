using System;
using System.Collections.Generic;
using UnityEngine;
using SurvivalWorld.Inventory;
using SurvivalWorld.Server.Inventory;
using SurvivalWorld.Shared;
using SurvivalWorld.Shared.Events;
using SurvivalWorld.Shared.MasterData;

namespace SurvivalWorld.Server.Simulation
{
    public sealed class ResourceNodeSystem
    {
        private readonly MasterDataStore masterData;
        private readonly M3InventoryService inventory;
        private readonly DomainEventFactory eventFactory;
        private readonly IInventoryEventSink eventSink;

        public ResourceNodeSystem(MasterDataStore masterData, M3InventoryService inventory, DomainEventFactory eventFactory, IInventoryEventSink eventSink)
        {
            this.masterData = masterData ?? throw new ArgumentNullException(nameof(masterData));
            this.inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
            this.eventFactory = eventFactory ?? throw new ArgumentNullException(nameof(eventFactory));
            this.eventSink = eventSink ?? NullInventoryEventSink.Instance;
        }

        public ResourceMineResult Mine(ResourceNodeState node, InventoryOwner actorInventory, ResourceMineRequest request)
        {
            if (node == null)
            {
                return ResourceMineResult.Rejected("Node is required.");
            }

            if (!Validate(node, request, out string error))
            {
                return ResourceMineResult.Rejected(error);
            }

            if (!masterData.TryGetResourceNode(node.ResourceType, out ResourceNodeDefinition definition))
            {
                return ResourceMineResult.Rejected("Unknown resource type: " + node.ResourceType);
            }

            int minedAmount = Math.Min(node.RemainingAmount, Math.Max(1, definition.BaseMineAmount + Math.Max(0, request.ToolQuality - 1)));
            var grant = new ItemStack(definition.GrantedItemDefinitionId, minedAmount);
            if (!inventory.CanGrant(actorInventory, grant.ItemDefinitionId, grant.ItemInstanceId, grant.Quantity, out string capacityReason))
            {
                return ResourceMineResult.Rejected(capacityReason);
            }

            M3InventoryResult grantResult = inventory.Grant(actorInventory, grant);
            if (!grantResult.Success)
            {
                return ResourceMineResult.Rejected(grantResult.Error);
            }

            node.RemainingAmount -= minedAmount;
            node.Version++;
            eventSink.Enqueue(eventFactory.Create(
                node.NodeId,
                DomainEventTypes.ResourceMined,
                JsonPayload.Object(
                    JsonPayload.Field("node_id", node.NodeId),
                    JsonPayload.Field("resource_type", node.ResourceType),
                    JsonPayload.Field("mined_amount", minedAmount),
                    JsonPayload.Raw("grants", JsonPayload.ItemStackArray(new[] { grant }, false)),
                    JsonPayload.Field("remaining_amount", node.RemainingAmount),
                    JsonPayload.Field("node_version", node.Version)),
                string.Empty,
                request.OccurredAtUnixMs));

            bool depleted = node.RemainingAmount == 0;
            if (depleted)
            {
                eventSink.Enqueue(eventFactory.Create(
                    node.NodeId,
                    DomainEventTypes.ResourceNodeDepleted,
                    JsonPayload.Object(
                        JsonPayload.Field("node_id", node.NodeId),
                        JsonPayload.Field("depleted_at_unix_ms", request.OccurredAtUnixMs)),
                    string.Empty,
                    request.OccurredAtUnixMs));
                node.NextRegenerationUnixMs = request.OccurredAtUnixMs + (node.RegenerationPolicy.CooldownSeconds * 1000L);
            }

            return ResourceMineResult.Accepted(minedAmount, node.RemainingAmount, node.Version, depleted);
        }

        public int TickRegeneration(IEnumerable<ResourceNodeState> nodes, long unixTimeMs)
        {
            int regenerated = 0;
            if (nodes == null)
            {
                return regenerated;
            }

            foreach (ResourceNodeState node in nodes)
            {
                if (node == null ||
                    node.RemainingAmount >= node.MaximumAmount ||
                    node.RegenerationPolicy.AmountPerMinute <= 0 ||
                    unixTimeMs < node.NextRegenerationUnixMs)
                {
                    continue;
                }

                int amount = Math.Min(node.MaximumAmount - node.RemainingAmount, node.RegenerationPolicy.AmountPerMinute);
                node.RemainingAmount += amount;
                node.Version++;
                node.NextRegenerationUnixMs = unixTimeMs + 60000L;
                regenerated++;
                eventSink.Enqueue(eventFactory.Create(
                    node.NodeId,
                    DomainEventTypes.ResourceNodeRegenerated,
                    JsonPayload.Object(
                        JsonPayload.Field("node_id", node.NodeId),
                        JsonPayload.Field("remaining_amount", node.RemainingAmount),
                        JsonPayload.Field("node_version", node.Version)),
                    string.Empty,
                    unixTimeMs));
            }

            return regenerated;
        }

        private bool Validate(ResourceNodeState node, ResourceMineRequest request, out string error)
        {
            error = string.Empty;
            if (node.RemainingAmount <= 0)
            {
                error = "Resource node is depleted.";
                return false;
            }

            if (request.ExpectedVersion != node.Version)
            {
                error = "Resource node version conflict.";
                return false;
            }

            if (Vector3.Distance(request.ActorPosition, node.Position) > SurvivalTuning.DefaultInteractionRange)
            {
                error = "Actor is too far from resource node.";
                return false;
            }

            if (!request.HasLineOfSight)
            {
                error = "Line of sight is blocked.";
                return false;
            }

            if (!masterData.TryGetResourceNode(node.ResourceType, out ResourceNodeDefinition definition))
            {
                error = "Unknown resource type: " + node.ResourceType;
                return false;
            }

            if (!string.IsNullOrWhiteSpace(definition.RequiredToolTag) && !request.HasToolTag(definition.RequiredToolTag))
            {
                error = "Required tool tag missing: " + definition.RequiredToolTag;
                return false;
            }

            return true;
        }
    }

    public sealed class ResourceNodeState
    {
        public ResourceNodeState(string nodeId, string resourceType, Vector3 position, int maximumAmount)
        {
            NodeId = nodeId ?? string.Empty;
            ResourceType = resourceType ?? string.Empty;
            Position = position;
            MaximumAmount = Math.Max(1, maximumAmount);
            RemainingAmount = MaximumAmount;
            RegenerationPolicy = new RegenerationPolicy(1, 60);
        }

        public string NodeId { get; }
        public string ResourceType { get; }
        public string RegionId { get; set; } = string.Empty;
        public Vector3 Position { get; set; }
        public int RemainingAmount { get; set; }
        public int MaximumAmount { get; set; }
        public int Quality { get; set; }
        public int Hardness { get; set; } = 1;
        public long Version { get; set; }
        public RegenerationPolicy RegenerationPolicy { get; set; }
        public string EventInstanceId { get; set; } = string.Empty;
        public long NextRegenerationUnixMs { get; set; }
    }

    public readonly struct RegenerationPolicy
    {
        public RegenerationPolicy(int amountPerMinute, int cooldownSeconds)
        {
            AmountPerMinute = amountPerMinute;
            CooldownSeconds = cooldownSeconds;
        }

        public int AmountPerMinute { get; }
        public int CooldownSeconds { get; }
    }

    public readonly struct ResourceMineRequest
    {
        public ResourceMineRequest(Vector3 actorPosition, long expectedVersion, bool hasLineOfSight, IEnumerable<string> equippedToolTags, int toolQuality, long occurredAtUnixMs)
        {
            ActorPosition = actorPosition;
            ExpectedVersion = expectedVersion;
            HasLineOfSight = hasLineOfSight;
            EquippedToolTags = equippedToolTags == null ? Array.Empty<string>() : new List<string>(equippedToolTags).ToArray();
            ToolQuality = toolQuality;
            OccurredAtUnixMs = occurredAtUnixMs;
        }

        public Vector3 ActorPosition { get; }
        public long ExpectedVersion { get; }
        public bool HasLineOfSight { get; }
        public IReadOnlyList<string> EquippedToolTags { get; }
        public int ToolQuality { get; }
        public long OccurredAtUnixMs { get; }

        public bool HasToolTag(string tag)
        {
            for (int i = 0; i < EquippedToolTags.Count; i++)
            {
                if (string.Equals(EquippedToolTags[i], tag ?? string.Empty, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }

    public readonly struct ResourceMineResult
    {
        private ResourceMineResult(bool success, int minedAmount, int remainingAmount, long nodeVersion, bool depleted, string error)
        {
            Success = success;
            MinedAmount = minedAmount;
            RemainingAmount = remainingAmount;
            NodeVersion = nodeVersion;
            Depleted = depleted;
            Error = error ?? string.Empty;
        }

        public bool Success { get; }
        public int MinedAmount { get; }
        public int RemainingAmount { get; }
        public long NodeVersion { get; }
        public bool Depleted { get; }
        public string Error { get; }

        public static ResourceMineResult Accepted(int minedAmount, int remainingAmount, long nodeVersion, bool depleted)
        {
            return new ResourceMineResult(true, minedAmount, remainingAmount, nodeVersion, depleted, string.Empty);
        }

        public static ResourceMineResult Rejected(string error)
        {
            return new ResourceMineResult(false, 0, 0, 0, false, error);
        }
    }
}

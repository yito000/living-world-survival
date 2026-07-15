using System;
using System.Collections.Generic;
using SurvivalWorld.Inventory;
using SurvivalWorld.Server.Inventory;
using SurvivalWorld.Shared.Events;
using SurvivalWorld.Shared.MasterData;
using SurvivalWorld.World;

namespace SurvivalWorld.Server.Simulation
{
    public sealed class StationJobSystem
    {
        private readonly MasterDataStore masterData;
        private readonly M3InventoryService inventory;
        private readonly DomainEventFactory eventFactory;
        private readonly IInventoryEventSink eventSink;
        private readonly HashSet<string> unlockedBlueprints = new HashSet<string>(StringComparer.Ordinal);

        public StationJobSystem(MasterDataStore masterData, M3InventoryService inventory, DomainEventFactory eventFactory, IInventoryEventSink eventSink)
        {
            this.masterData = masterData ?? throw new ArgumentNullException(nameof(masterData));
            this.inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
            this.eventFactory = eventFactory ?? throw new ArgumentNullException(nameof(eventFactory));
            this.eventSink = eventSink ?? NullInventoryEventSink.Instance;
        }

        public IReadOnlyCollection<string> UnlockedBlueprints => unlockedBlueprints;

        public void UnlockBlueprint(string blueprintId)
        {
            if (!string.IsNullOrWhiteSpace(blueprintId))
            {
                unlockedBlueprints.Add(blueprintId);
            }
        }

        public StationJobResult StartJob(StationState station, InventoryOwner actorInventory, string actorId, string recipeId, long unixTimeMs)
        {
            if (station == null)
            {
                return StationJobResult.Rejected("Station is required.");
            }

            if (station.CurrentJob != null)
            {
                return StationJobResult.Rejected("Station already has a job.");
            }

            if (!masterData.TryGetRecipe(recipeId, out RecipeDefinition recipe))
            {
                return StationJobResult.Rejected("Unknown recipe: " + recipeId);
            }

            if (!string.Equals(recipe.StationType, station.StationType, StringComparison.Ordinal))
            {
                return StationJobResult.Rejected("Recipe cannot run on station type " + station.StationType);
            }

            if (recipe.RequiresBlueprint && !unlockedBlueprints.Contains(recipe.RequiredBlueprintId))
            {
                return StationJobResult.Rejected("Blueprint is locked: " + recipe.RequiredBlueprintId);
            }

            M3InventoryResult reserve = inventory.Reserve(actorInventory, recipe.Ingredients);
            if (!reserve.Success)
            {
                return StationJobResult.Rejected(reserve.Error);
            }

            station.CurrentJob = new StationJobState(recipe.RecipeId, actorId, unixTimeMs, unixTimeMs + (recipe.DurationSeconds * 1000L));
            eventSink.Enqueue(eventFactory.Create(
                station.StationId,
                DomainEventTypes.StationJobStarted,
                JsonPayload.Object(
                    JsonPayload.Field("station_id", station.StationId),
                    JsonPayload.Field("recipe_id", recipe.RecipeId),
                    JsonPayload.Field("actor_id", actorId),
                    JsonPayload.Raw("reserved", JsonPayload.ItemStackArray(recipe.Ingredients, false))),
                string.Empty,
                unixTimeMs));
            return StationJobResult.Started(station.CurrentJob.CompleteAtUnixMs);
        }

        public StationJobResult CancelJob(StationState station, InventoryOwner actorInventory, long unixTimeMs)
        {
            if (station == null || station.CurrentJob == null)
            {
                return StationJobResult.Rejected("No active station job.");
            }

            if (!masterData.TryGetRecipe(station.CurrentJob.RecipeId, out RecipeDefinition recipe))
            {
                return StationJobResult.Rejected("Unknown recipe: " + station.CurrentJob.RecipeId);
            }

            M3InventoryResult released = inventory.ReleaseReserved(actorInventory, recipe.Ingredients);
            if (!released.Success)
            {
                return StationJobResult.Rejected(released.Error);
            }

            string actorId = station.CurrentJob.ActorId;
            station.CurrentJob = null;
            eventSink.Enqueue(eventFactory.Create(
                station.StationId,
                DomainEventTypes.StationJobCancelled,
                JsonPayload.Object(
                    JsonPayload.Field("station_id", station.StationId),
                    JsonPayload.Field("recipe_id", recipe.RecipeId),
                    JsonPayload.Field("actor_id", actorId),
                    JsonPayload.Raw("released", JsonPayload.ItemStackArray(recipe.Ingredients, false))),
                string.Empty,
                unixTimeMs));
            return StationJobResult.Cancelled();
        }

        public StationJobResult CompleteReadyJob(StationState station, InventoryOwner actorInventory, long unixTimeMs)
        {
            if (station == null || station.CurrentJob == null)
            {
                return StationJobResult.Rejected("No active station job.");
            }

            if (unixTimeMs < station.CurrentJob.CompleteAtUnixMs)
            {
                return StationJobResult.Rejected("Station job is not ready.");
            }

            if (!masterData.TryGetRecipe(station.CurrentJob.RecipeId, out RecipeDefinition recipe))
            {
                return StationJobResult.Rejected("Unknown recipe: " + station.CurrentJob.RecipeId);
            }

            string actorId = station.CurrentJob.ActorId;
            M3InventoryResult consumed = inventory.ConsumeReserved(actorInventory, recipe.Ingredients);
            if (!consumed.Success)
            {
                return StationJobResult.Rejected(consumed.Error);
            }

            var produced = new List<ItemStack>();
            foreach (ItemStack output in recipe.Outputs)
            {
                string instanceId = ShouldCreateInstanceId(output.ItemDefinitionId, output.Quantity) ? DomainEventId.NewUlid(unixTimeMs) : string.Empty;
                var stack = new ItemStack(output.ItemDefinitionId, output.Quantity, instanceId);
                if (inventory.CanGrant(actorInventory, stack.ItemDefinitionId, stack.ItemInstanceId, stack.Quantity, out _))
                {
                    inventory.Grant(actorInventory, stack);
                }
                else
                {
                    station.Outputs.Add(stack);
                }

                produced.Add(stack);
            }

            station.CurrentJob = null;
            if (recipe.UnlocksBlueprint)
            {
                unlockedBlueprints.Add(recipe.UnlockedBlueprintId);
                eventSink.Enqueue(eventFactory.Create(
                    "world",
                    DomainEventTypes.DevelopmentBlueprintUnlocked,
                    JsonPayload.Object(
                        JsonPayload.Field("blueprint_id", recipe.UnlockedBlueprintId),
                        JsonPayload.Field("recipe_id", recipe.RecipeId)),
                    string.Empty,
                    unixTimeMs));
            }

            string eventType = recipe.Kind == RecipeKind.Cooking ? DomainEventTypes.CookingCompleted : DomainEventTypes.StationJobCompleted;
            eventSink.Enqueue(eventFactory.Create(
                station.StationId,
                eventType,
                JsonPayload.Object(
                    JsonPayload.Field("station_id", station.StationId),
                    JsonPayload.Field("recipe_id", recipe.RecipeId),
                    JsonPayload.Field("actor_id", actorId),
                    JsonPayload.Raw("consumed", JsonPayload.ItemStackArray(recipe.Ingredients, false)),
                    JsonPayload.Raw("produced", JsonPayload.ItemStackArray(produced, true))),
                string.Empty,
                unixTimeMs));
            return StationJobResult.Completed(produced.Count);
        }

        private static bool ShouldCreateInstanceId(string itemDefinitionId, int quantity)
        {
            return quantity == 1 &&
                   (string.Equals(itemDefinitionId, "stone_pickaxe", StringComparison.Ordinal) ||
                    string.Equals(itemDefinitionId, "stone_spear", StringComparison.Ordinal) ||
                    string.Equals(itemDefinitionId, "iron_hunting_spear", StringComparison.Ordinal) ||
                    string.Equals(itemDefinitionId, "rare_weapon", StringComparison.Ordinal));
        }
    }

    public sealed class StationState
    {
        public StationState(string stationId, string stationType)
        {
            StationId = stationId ?? string.Empty;
            StationType = stationType ?? string.Empty;
        }

        public string StationId { get; }
        public string StationType { get; }
        public StationJobState CurrentJob { get; set; }
        public List<ItemStack> Outputs { get; } = new List<ItemStack>();
    }

    public sealed class StationJobState
    {
        public StationJobState(string recipeId, string actorId, long startedAtUnixMs, long completeAtUnixMs)
        {
            RecipeId = recipeId ?? string.Empty;
            ActorId = actorId ?? string.Empty;
            StartedAtUnixMs = startedAtUnixMs;
            CompleteAtUnixMs = completeAtUnixMs;
        }

        public string RecipeId { get; }
        public string ActorId { get; }
        public long StartedAtUnixMs { get; }
        public long CompleteAtUnixMs { get; }
    }

    public readonly struct StationJobResult
    {
        private StationJobResult(bool success, string status, long completeAtUnixMs, int producedCount, string error)
        {
            Success = success;
            Status = status ?? string.Empty;
            CompleteAtUnixMs = completeAtUnixMs;
            ProducedCount = producedCount;
            Error = error ?? string.Empty;
        }

        public bool Success { get; }
        public string Status { get; }
        public long CompleteAtUnixMs { get; }
        public int ProducedCount { get; }
        public string Error { get; }

        public static StationJobResult Started(long completeAtUnixMs)
        {
            return new StationJobResult(true, "started", completeAtUnixMs, 0, string.Empty);
        }

        public static StationJobResult Completed(int producedCount)
        {
            return new StationJobResult(true, "completed", 0, producedCount, string.Empty);
        }

        public static StationJobResult Cancelled()
        {
            return new StationJobResult(true, "cancelled", 0, 0, string.Empty);
        }

        public static StationJobResult Rejected(string error)
        {
            return new StationJobResult(false, "rejected", 0, 0, error);
        }
    }
}

using System;
using SurvivalWorld.Inventory;
using SurvivalWorld.Items;
using SurvivalWorld.Server.Inventory;
using SurvivalWorld.Shared;
using SurvivalWorld.Shared.Events;
using SurvivalWorld.Shared.MasterData;

namespace SurvivalWorld.Server.Simulation
{
    public sealed class CharacterSimulation
    {
        private readonly DomainEventFactory eventFactory;
        private readonly IInventoryEventSink eventSink;
        private readonly M3InventoryService inventory;
        private readonly IItemDefinitionCatalog catalog;

        public CharacterSimulation(DomainEventFactory eventFactory, IInventoryEventSink eventSink, M3InventoryService inventory, IItemDefinitionCatalog catalog)
        {
            this.eventFactory = eventFactory ?? throw new ArgumentNullException(nameof(eventFactory));
            this.eventSink = eventSink ?? NullInventoryEventSink.Instance;
            this.inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
            this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        }

        public CharacterTickResult Tick(CharacterVitalsState state, float deltaSeconds, long occurredAtUnixMs)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            CharacterVitalsBand beforeBand = GetBand(state.Hunger);
            float beforeHealth = state.Health;
            state.Hunger = Math.Max(0f, state.Hunger - (SurvivalTuning.HungerDecayPerSecond * Math.Max(0f, deltaSeconds)));
            CharacterVitalsBand afterBand = GetBand(state.Hunger);

            bool emitted = false;
            if (beforeBand != afterBand)
            {
                EmitVitalsChanged(state, afterBand.ToString().ToLowerInvariant(), occurredAtUnixMs);
                emitted = true;
            }

            if (state.Hunger <= 0f)
            {
                state.StarvationAccumulatorSeconds += Math.Max(0f, deltaSeconds);
                while (state.StarvationAccumulatorSeconds >= SurvivalTuning.StarvationHealthDrainIntervalSeconds && state.Health > 0f)
                {
                    state.StarvationAccumulatorSeconds -= SurvivalTuning.StarvationHealthDrainIntervalSeconds;
                    state.Health = Math.Max(0f, state.Health - SurvivalTuning.StarvationHealthDrain);
                    EmitVitalsChanged(state, "starvation", occurredAtUnixMs);
                    emitted = true;
                }
            }
            else
            {
                state.StarvationAccumulatorSeconds = 0f;
            }

            return new CharacterTickResult(beforeBand, afterBand, beforeHealth, state.Health, emitted);
        }

        public M3CommandResult ConsumeFood(CharacterVitalsState state, InventoryOwner inventoryOwner, string itemDefinitionId, long occurredAtUnixMs)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (!catalog.TryGet(itemDefinitionId, out ItemDefinitionData definition) || !definition.UseEffect.HasEffect)
            {
                return M3CommandResult.Rejected("Item has no hunger use effect.");
            }

            M3InventoryResult consumed = inventory.ConsumeAvailable(inventoryOwner, itemDefinitionId, 1);
            if (!consumed.Success)
            {
                return M3CommandResult.Rejected(consumed.Error);
            }

            int hungerDelta = definition.UseEffect.HungerDelta;
            state.Hunger = Math.Min(SurvivalTuning.HungerInitial, state.Hunger + hungerDelta);
            eventSink.Enqueue(eventFactory.Create(
                state.ActorId,
                DomainEventTypes.InventoryItemConsumed,
                JsonPayload.Object(
                    JsonPayload.Field("actor_id", state.ActorId),
                    JsonPayload.Field("item_definition_id", itemDefinitionId),
                    JsonPayload.Field("hunger_delta", hungerDelta)),
                occurredAtUnixMs: occurredAtUnixMs,
                eventId: string.Empty));
            EmitVitalsChanged(state, "consume", occurredAtUnixMs);
            return M3CommandResult.Ok();
        }

        public static CharacterVitalsBand GetBand(float hunger)
        {
            if (hunger <= 0f)
            {
                return CharacterVitalsBand.Starvation;
            }

            if (hunger < SurvivalTuning.HungerCritical)
            {
                return CharacterVitalsBand.Critical;
            }

            if (hunger < SurvivalTuning.HungerWarning)
            {
                return CharacterVitalsBand.Warning;
            }

            return CharacterVitalsBand.Normal;
        }

        private void EmitVitalsChanged(CharacterVitalsState state, string cause, long occurredAtUnixMs)
        {
            eventSink.Enqueue(eventFactory.Create(
                state.ActorId,
                DomainEventTypes.CharacterVitalsChanged,
                JsonPayload.Object(
                    JsonPayload.Field("actor_id", state.ActorId),
                    JsonPayload.Field("hunger", state.Hunger),
                    JsonPayload.Field("health", state.Health),
                    JsonPayload.Field("cause", cause)),
                string.Empty,
                occurredAtUnixMs));
        }
    }

    public sealed class CharacterVitalsState
    {
        public CharacterVitalsState(string actorId)
        {
            ActorId = string.IsNullOrWhiteSpace(actorId) ? "actor" : actorId;
            Hunger = SurvivalTuning.HungerInitial;
            Health = SurvivalTuning.HealthInitial;
        }

        public string ActorId { get; }
        public float Hunger { get; set; }
        public float Health { get; set; }
        public float StarvationAccumulatorSeconds { get; set; }
    }

    public enum CharacterVitalsBand
    {
        Normal,
        Warning,
        Critical,
        Starvation
    }

    public readonly struct CharacterTickResult
    {
        public CharacterTickResult(CharacterVitalsBand beforeBand, CharacterVitalsBand afterBand, float beforeHealth, float afterHealth, bool emittedEvent)
        {
            BeforeBand = beforeBand;
            AfterBand = afterBand;
            BeforeHealth = beforeHealth;
            AfterHealth = afterHealth;
            EmittedEvent = emittedEvent;
        }

        public CharacterVitalsBand BeforeBand { get; }
        public CharacterVitalsBand AfterBand { get; }
        public float BeforeHealth { get; }
        public float AfterHealth { get; }
        public bool EmittedEvent { get; }
    }
}

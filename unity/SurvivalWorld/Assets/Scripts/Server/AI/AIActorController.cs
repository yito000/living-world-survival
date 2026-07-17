using System;
using System.Collections.Generic;
using SurvivalWorld.Inventory;

namespace SurvivalWorld.Server.AI
{
    public sealed class AIActorController
    {
        private readonly PrimitiveActionRegistry registry;
        private readonly Dictionary<string, string> blackboard = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> activeInterrupts = new HashSet<string>(StringComparer.Ordinal);

        public AIActorController(string actorId, AIInventoryAdapter inventory, PrimitiveActionRegistry registry)
        {
            ActorId = string.IsNullOrWhiteSpace(actorId) ? "ai" : actorId;
            Inventory = inventory;
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            PersonalState = new AIPersonalState(ActorId);
            Runner = new ActionTemplateRunner();
        }

        public string ActorId { get; }
        public AIPersonalState PersonalState { get; }
        public AIInventoryAdapter Inventory { get; }
        public ActionTemplateRunner Runner { get; }
        public PrimitiveActionRegistry Registry => registry;
        public IDictionary<string, string> Blackboard => blackboard;
        public ISet<string> ActiveInterrupts => activeInterrupts;

        public void RefreshPersonalState(float hunger, float nearbyWasteWeight, long wealthGoal)
        {
            AIInventorySummary summary = Inventory == null
                ? new AIInventorySummary(PersonalState.UsedSlots, Math.Max(0, PersonalState.CapacitySlots - PersonalState.UsedSlots), PersonalState.CapacitySlots, PersonalState.SellableCount, PersonalState.NetWorth)
                : Inventory.GetSummary();
            PersonalState.UpdateFromInventory(hunger, summary, nearbyWasteWeight, wealthGoal);
        }

        public ActionTemplateStartResult ApplyTemplate(ActionTemplateDefinition template, long leaseUntilUnixMs, long unixTimeMs)
        {
            ActionTemplateStartResult start = Runner.Start(template, registry, CreatePreconditionContext(), unixTimeMs);
            if (start.Success)
            {
                PersonalState.StartTemplate(template.TemplateId, template.Version, unixTimeMs, leaseUntilUnixMs);
            }

            return start;
        }

        public ActionTemplateTickResult Tick(long unixTimeMs, float deltaSeconds)
        {
            var context = new PrimitiveActionContext(ActorId, PersonalState, Inventory, blackboard, activeInterrupts, unixTimeMs, deltaSeconds);
            ActionTemplateTickResult result = Runner.Tick(registry, context);
            if (result.Status == RunnerTickStatus.Completed)
            {
                PersonalState.ClearTemplate(unixTimeMs);
            }
            else if (result.Status == RunnerTickStatus.Failed || result.Status == RunnerTickStatus.Interrupted)
            {
                PersonalState.RecordFailure();
                PersonalState.ClearTemplate(unixTimeMs);
            }

            return result;
        }

        public void CancelTemplate(string reason, long unixTimeMs)
        {
            Runner.Cancel(reason, unixTimeMs);
            PersonalState.ClearTemplate(unixTimeMs);
        }

        public AIPreconditionContext CreatePreconditionContext()
        {
            AIInventorySummary summary = Inventory == null
                ? new AIInventorySummary(PersonalState.UsedSlots, Math.Max(0, PersonalState.CapacitySlots - PersonalState.UsedSlots), PersonalState.CapacitySlots, PersonalState.SellableCount, PersonalState.NetWorth)
                : Inventory.GetSummary();
            return new AIPreconditionContext(PersonalState, summary);
        }
    }
}

using System;
using System.Collections.Generic;
using SurvivalWorld.Inventory;

namespace SurvivalWorld.Server.AI
{
    public delegate PrimitiveActionResult PrimitiveActionHandler(PrimitiveActionContext context);

    public sealed class PrimitiveActionRegistry
    {
        private readonly Dictionary<string, PrimitiveActionHandler> handlers = new Dictionary<string, PrimitiveActionHandler>(StringComparer.Ordinal);

        public void Register(string primitiveName, PrimitiveActionHandler handler)
        {
            if (string.IsNullOrWhiteSpace(primitiveName))
            {
                throw new ArgumentException("Primitive name is required.", nameof(primitiveName));
            }

            handlers[primitiveName] = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        public bool Contains(string primitiveName)
        {
            return handlers.ContainsKey(primitiveName ?? string.Empty);
        }

        public PrimitiveActionResult Execute(ActionStepSpec step, PrimitiveActionContext context)
        {
            if (!handlers.TryGetValue(step.Name ?? string.Empty, out PrimitiveActionHandler handler))
            {
                return PrimitiveActionResult.Failed("Unknown primitive: " + step.Name, false);
            }

            context.CurrentStep = step;
            return handler(context);
        }

        public static PrimitiveActionRegistry CreateM4Defaults()
        {
            var registry = new PrimitiveActionRegistry();
            registry.RegisterCompleted("MoveTo");
            registry.RegisterCompleted("Interact");
            registry.RegisterCompleted("FindAnimal");
            registry.RegisterCompleted("HuntAnimal");
            registry.RegisterCompleted("ButcherCarcass");
            registry.RegisterCompleted("FindResourceNode");
            registry.RegisterCompleted("MineIron");
            registry.RegisterCompleted("CraftStoneSpear");
            registry.RegisterCompleted("ResearchIronSpear");
            registry.RegisterCompleted("CraftIronSpear");
            registry.RegisterCompleted("PurchaseStub");
            registry.RegisterCompleted("SellStub");
            registry.RegisterCompleted("DiscardLowValue");
            registry.RegisterCompleted("FindWaste");
            registry.RegisterCompleted("CleanNearby");
            registry.RegisterCompleted("WorldEventStub");
            registry.RegisterCompleted("Wait");
            registry.Register("SelectOwnedFood", SelectOwnedFood);
            registry.Register("UseItem", UseItem);
            registry.Register("CookRawMeat", Complete);
            return registry;
        }

        public void RegisterCompleted(string primitiveName)
        {
            Register(primitiveName, Complete);
        }

        private static PrimitiveActionResult Complete(PrimitiveActionContext context)
        {
            return PrimitiveActionResult.Completed();
        }

        private static PrimitiveActionResult SelectOwnedFood(PrimitiveActionContext context)
        {
            if (context.Inventory != null && context.Inventory.TryFindFirstUsableFood(out string itemDefinitionId))
            {
                context.Blackboard["item_definition_id"] = itemDefinitionId;
                return PrimitiveActionResult.Completed();
            }

            return PrimitiveActionResult.Failed("No usable food is owned.", false);
        }

        private static PrimitiveActionResult UseItem(PrimitiveActionContext context)
        {
            string itemDefinitionId = context.GetParameter("item_definition_id");
            if (string.IsNullOrWhiteSpace(itemDefinitionId))
            {
                context.Blackboard.TryGetValue("item_definition_id", out itemDefinitionId);
            }

            if (string.IsNullOrWhiteSpace(itemDefinitionId))
            {
                return PrimitiveActionResult.Failed("item_definition_id is required.", false);
            }

            if (context.PersonalState != null)
            {
                AIInventorySummary summary = context.Inventory == null ? default : context.Inventory.GetSummary();
                context.PersonalState.UpdateFromInventory(
                    Math.Min(100f, context.PersonalState.Hunger + 30f),
                    summary,
                    context.PersonalState.NearbyWasteWeight,
                    context.PersonalState.WealthGoal);
            }

            return PrimitiveActionResult.Completed();
        }
    }

    public sealed class PrimitiveActionContext
    {
        private readonly List<Action> compensations = new List<Action>();

        public PrimitiveActionContext(
            string actorId,
            AIPersonalState personalState,
            AIInventoryAdapter inventory,
            IDictionary<string, string> blackboard,
            ISet<string> activeInterrupts,
            long unixTimeMs,
            float deltaSeconds)
        {
            ActorId = actorId ?? string.Empty;
            PersonalState = personalState;
            Inventory = inventory;
            Blackboard = blackboard ?? new Dictionary<string, string>(StringComparer.Ordinal);
            ActiveInterrupts = activeInterrupts ?? new HashSet<string>(StringComparer.Ordinal);
            UnixTimeMs = unixTimeMs;
            DeltaSeconds = deltaSeconds;
        }

        public string ActorId { get; }
        public AIPersonalState PersonalState { get; }
        public AIInventoryAdapter Inventory { get; }
        public IDictionary<string, string> Blackboard { get; }
        public ISet<string> ActiveInterrupts { get; }
        public long UnixTimeMs { get; }
        public float DeltaSeconds { get; }
        public ActionStepSpec CurrentStep { get; internal set; }
        public IReadOnlyList<Action> Compensations => compensations;

        public string GetParameter(string key)
        {
            if (CurrentStep.Parameters != null && CurrentStep.Parameters.TryGetValue(key, out string value))
            {
                return value;
            }

            return Blackboard.TryGetValue(key, out value) ? value : string.Empty;
        }

        public void RegisterCompensation(Action compensation)
        {
            if (compensation != null)
            {
                compensations.Add(compensation);
            }
        }
    }

    public readonly struct PrimitiveActionResult
    {
        private PrimitiveActionResult(PrimitiveActionStatus status, string error, bool retryable)
        {
            Status = status;
            Error = error ?? string.Empty;
            Retryable = retryable;
        }

        public PrimitiveActionStatus Status { get; }
        public string Error { get; }
        public bool Retryable { get; }
        public bool IsTerminalFailure => Status == PrimitiveActionStatus.Failed;

        public static PrimitiveActionResult Running()
        {
            return new PrimitiveActionResult(PrimitiveActionStatus.Running, string.Empty, true);
        }

        public static PrimitiveActionResult Completed()
        {
            return new PrimitiveActionResult(PrimitiveActionStatus.Completed, string.Empty, false);
        }

        public static PrimitiveActionResult Failed(string error, bool retryable)
        {
            return new PrimitiveActionResult(PrimitiveActionStatus.Failed, error, retryable);
        }
    }

    public enum PrimitiveActionStatus
    {
        Running = 0,
        Completed = 1,
        Failed = 2
    }
}

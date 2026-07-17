using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Survival.V1;
using SurvivalWorld.Inventory;
using SurvivalWorld.Items;
using SurvivalWorld.Shared;

namespace SurvivalWorld.Server.AI
{
    public sealed class AIActorSystem
    {
        public const int DefaultActorCount = 20;

        private readonly List<AIActorController> actors = new List<AIActorController>();
        private readonly Dictionary<string, AIActorController> actorsById = new Dictionary<string, AIActorController>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> lastDecisionRequestUnixMsByActor = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly ActionTemplateCatalog templates;
        private readonly PrimitiveActionRegistry registry;
        private readonly UtilityFallback fallback;
        private readonly AIDecisionClient decisionClient;
        private readonly AIActorRuntimeStatePersistence persistence;
        private int cursor;

        public AIActorSystem(ActionTemplateCatalog templates, PrimitiveActionRegistry registry, UtilityFallback fallback, AIDecisionClient decisionClient, AIActorRuntimeStatePersistence persistence)
        {
            this.templates = templates ?? throw new ArgumentNullException(nameof(templates));
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            this.fallback = fallback ?? new UtilityFallback();
            this.decisionClient = decisionClient;
            this.persistence = persistence ?? new AIActorRuntimeStatePersistence(NullActorStateGateway.Instance);
            if (this.decisionClient != null)
            {
                this.decisionClient.DecisionReceived += OnDecisionReceived;
                this.decisionClient.Start();
            }
        }

        public IReadOnlyList<AIActorController> Actors => actors;

        public static AIActorSystem CreateDefault(string serverId, string worldId, IItemDefinitionCatalog itemCatalog, IActorStateGateway actorStateGateway, IAIDecisionTransport decisionTransport = null)
        {
            ActionTemplateCatalog templateCatalog = new ActionTemplateCatalog(ActionTemplateDefinition.CreateM4Defaults());
            PrimitiveActionRegistry primitiveRegistry = PrimitiveActionRegistry.CreateM4Defaults();
            RegisterDefaultMutatingPrimitives(primitiveRegistry);
            var decisionClient = new AIDecisionClient(serverId, worldId, templateCatalog, decisionTransport ?? NullAIDecisionTransport.Instance, primitiveRegistry);
            var system = new AIActorSystem(templateCatalog, primitiveRegistry, new UtilityFallback(), decisionClient, new AIActorRuntimeStatePersistence(actorStateGateway, worldId));
            system.SpawnActors(DefaultActorCount, itemCatalog, worldId, serverId == null ? 0 : serverId.GetHashCode());
            return system;
        }

        public void SpawnActors(int count, IItemDefinitionCatalog itemCatalog, string worldId, int seed)
        {
            if (count <= 0)
            {
                return;
            }

            IItemDefinitionCatalog catalog = itemCatalog ?? ItemDefinitionCatalog.CreateMvpCatalog();
            var inventoryService = new InventoryService(catalog, NullInventoryEventSink.Instance, string.IsNullOrWhiteSpace(worldId) ? "runtime" : worldId);
            for (int i = 0; i < count; i++)
            {
                string actorId = "ai-" + (actors.Count + 1).ToString("00", System.Globalization.CultureInfo.InvariantCulture);
                var owner = new InventoryOwner("ai", actorId, InventoryOwner.DefaultSlotCapacity, InventoryOwner.DefaultWeightCapacity, 0L);
                var adapter = new AIInventoryAdapter(inventoryService, owner, catalog);
                SeedInventory(adapter, i, seed);
                var actor = new AIActorController(actorId, adapter, registry);
                actor.RefreshPersonalState(80f - (i % 5) * 12f, i % 4, 100L);
                actors.Add(actor);
                actorsById[actorId] = actor;
            }
        }

        public int Tick(long unixTimeMs, float deltaSeconds, int actorBudget)
        {
            if (actors.Count == 0)
            {
                return 0;
            }

            decisionClient?.DispatchQueuedDecisions();

            int budget = actorBudget <= 0 ? actors.Count : Math.Min(actorBudget, actors.Count);
            int processed = 0;
            for (int i = 0; i < budget; i++)
            {
                AIActorController actor = actors[cursor];
                cursor = (cursor + 1) % actors.Count;
                TickActor(actor, unixTimeMs, deltaSeconds);
                processed++;
            }

            return processed;
        }

        public async UniTask RunAsync(TimeSpan tickInterval, TimeSpan persistenceInterval, CancellationToken cancellationToken)
        {
            TimeSpan safeTick = tickInterval <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(50) : tickInterval;
            TimeSpan safePersistence = persistenceInterval <= TimeSpan.Zero ? TimeSpan.FromSeconds(10) : persistenceInterval;
            long nextSaveUnixMs = NowUnixMs() + (long)safePersistence.TotalMilliseconds;
            while (!cancellationToken.IsCancellationRequested)
            {
                await UniTask.Delay(safeTick, cancellationToken: cancellationToken);
                long now = NowUnixMs();
                Tick(now, (float)safeTick.TotalSeconds, Math.Max(1, actors.Count / 4));
                if (now >= nextSaveUnixMs)
                {
                    await SaveAllAsync(cancellationToken);
                    nextSaveUnixMs = now + (long)safePersistence.TotalMilliseconds;
                }
            }
        }

        public async UniTask SaveAllAsync(CancellationToken cancellationToken)
        {
            for (int i = 0; i < actors.Count; i++)
            {
                SaveResponse response = await persistence.SaveAsync(actors[i], cancellationToken);
                if (response.Status != ResultStatus.Ok && response.Status != ResultStatus.Duplicate)
                {
                    UnityEngine.Debug.LogWarning("ActorState.Save rejected for " + actors[i].ActorId + ": " + response.Status);
                }
            }
        }

        private void TickActor(AIActorController actor, long unixTimeMs, float deltaSeconds)
        {
            float nextHunger = Math.Max(0f, actor.PersonalState.Hunger - SurvivalTuning.HungerDecayPerSecond * Math.Max(0f, deltaSeconds));
            actor.RefreshPersonalState(nextHunger, actor.PersonalState.NearbyWasteWeight, actor.PersonalState.WealthGoal);
            ActionTemplateTickResult tick = actor.Tick(unixTimeMs, deltaSeconds);

            bool needsDecision = !actor.Runner.HasActiveTemplate || actor.PersonalState.ActionState.LeaseUntilUnixMs <= unixTimeMs;
            if (!needsDecision)
            {
                return;
            }

            if (ShouldRequestDecision(actor.ActorId, unixTimeMs))
            {
                decisionClient?.PublishRequest(actor, tick.Status == RunnerTickStatus.Completed ? "template_completed" : "utility_fallback");
                lastDecisionRequestUnixMsByActor[actor.ActorId] = unixTimeMs;
            }

            ActionTemplateDefinition fallbackTemplate = fallback.SelectTemplate(actor.PersonalState, templates);
            if (fallbackTemplate != null && actor.Runner.CanSwitchTemplate(unixTimeMs))
            {
                long leaseUntil = unixTimeMs + fallbackTemplate.MaxDurationSeconds * 1000L;
                ActionTemplateStartResult start = actor.ApplyTemplate(fallbackTemplate, leaseUntil, unixTimeMs);
                if (!start.Success)
                {
                    actor.PersonalState.RecordFailure();
                }
            }
        }

        private bool ShouldRequestDecision(string actorId, long unixTimeMs)
        {
            if (!lastDecisionRequestUnixMsByActor.TryGetValue(actorId, out long lastRequest))
            {
                return true;
            }

            return unixTimeMs - lastRequest >= 10000L;
        }

        private void OnDecisionReceived(ActionDecision decision)
        {
            if (decision == null || !actorsById.TryGetValue(decision.ActorId, out AIActorController actor))
            {
                return;
            }

            DecisionApplicationResult result = decisionClient.ApplyDecision(decision, actor, NowUnixMs());
            if (!result.Success)
            {
                UnityEngine.Debug.LogWarning("AI decision rejected: " + result.Error);
            }
            else if (result.Status == DecisionApplicationStatus.Applied)
            {
                UnityEngine.Debug.Log("AI decision applied: actor=" + actor.ActorId + ", template=" + result.TemplateId + ", decision_id=" + result.DecisionId);
            }
        }

        public void Stop()
        {
            decisionClient?.Stop();
        }

        private static void SeedInventory(AIInventoryAdapter adapter, int index, int seed)
        {
            adapter.AddLoot("stone_pickaxe", 1);
            if ((index + seed) % 3 == 0)
            {
                adapter.AddLoot("cooked_meat", 1);
            }

            if (index % 2 == 0)
            {
                adapter.AddLoot("stone", 5);
                adapter.AddLoot("wood", 2);
            }
        }

        private static void RegisterDefaultMutatingPrimitives(PrimitiveActionRegistry registry)
        {
            registry.Register("MineIron", context => AddLoot(context, "iron_ore", 1));
            registry.Register("ButcherCarcass", context => AddLoot(context, "raw_meat", 1));
            registry.Register("CookRawMeat", context => AddLoot(context, "cooked_meat", 1));
            registry.Register("CraftStoneSpear", context => AddLoot(context, "stone_spear", 1));
            registry.Register("CraftIronSpear", context => AddLoot(context, "iron_hunting_spear", 1));
            registry.Register("ResearchIronSpear", context => PrimitiveActionResult.Completed());
            registry.Register("DiscardLowValue", context => PrimitiveActionResult.Completed());
            registry.Register("CleanNearby", context => PrimitiveActionResult.Completed());
        }

        private static PrimitiveActionResult AddLoot(PrimitiveActionContext context, string itemDefinitionId, int quantity)
        {
            if (context.Inventory == null)
            {
                return PrimitiveActionResult.Failed("Inventory is required.", false);
            }

            InventoryMutationResult result = context.Inventory.AddLoot(itemDefinitionId, quantity);
            return result.Success ? PrimitiveActionResult.Completed() : PrimitiveActionResult.Failed(result.Error, false);
        }

        private static long NowUnixMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }
}

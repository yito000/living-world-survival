using NUnit.Framework;
using Survival.V1;
using SurvivalWorld.Inventory;
using SurvivalWorld.Items;
using SurvivalWorld.Server.AI;
using SurvivalWorld.Shared.MasterData;

namespace SurvivalWorld.Tests
{
    public sealed class M4AIEditModeTests
    {
        [Test]
        public void PersonalStateUrgencyFormulasClampAtBoundaries()
        {
            Assert.AreEqual(1f, AIPersonalState.UrgencyFood(0f));
            Assert.AreEqual(0f, AIPersonalState.UrgencyFood(60f));
            Assert.AreEqual(0f, AIPersonalState.UrgencyFood(120f));
            Assert.AreEqual(0f, AIPersonalState.UrgencyCleanup(24, 24));
            Assert.AreEqual(1f, AIPersonalState.UrgencyCleanup(48, 24));
            Assert.AreEqual(1f, AIPersonalState.UrgencyEarn(100, 0));
            Assert.AreEqual(0f, AIPersonalState.UrgencyEarn(100, 150));
            Assert.AreEqual(0.5f, AIPersonalState.NeedScore(60f, 30f));

            AIUrgencySnapshot snapshot = AIPersonalState.CalculateUrgency(30f, 12, 24, 25, 100, 5f, 10f);
            Assert.AreEqual(0.5f, snapshot.Food);
            Assert.AreEqual(0f, snapshot.Cleanup);
            Assert.AreEqual(0.75f, snapshot.Earn);
            Assert.AreEqual(0.75f, snapshot.NeedScore);
            Assert.AreEqual(0.5f, snapshot.InventoryPressure);
            Assert.AreEqual(0.5f, snapshot.CleanlinessPressure);
        }

        [Test]
        public void TemplateParserReadsDefinitionAndRejectsUnknownPrimitive()
        {
            string json = "{\"template_id\":\"economy.sell_surplus\",\"version\":3,\"tags\":[\"wealth\"],\"preconditions\":[\"inventory.free_slots < 3\"],\"interrupts\":[\"path_failed\"],\"steps\":[\"SelectSellableItem\",\"FindBuyer\"],\"min_duration_sec\":20,\"max_duration_sec\":600}";

            Assert.IsTrue(ActionTemplateDefinition.TryParseJson(json, out ActionTemplateDefinition definition, out string error), error);
            Assert.AreEqual("economy.sell_surplus", definition.TemplateId);
            Assert.AreEqual(3, definition.Version);
            Assert.AreEqual(2, definition.Steps.Count);
            Assert.AreEqual(20, definition.MinDurationSeconds);

            PrimitiveActionRegistry registry = PrimitiveActionRegistry.CreateM4Defaults();
            Assert.IsFalse(definition.ValidatePrimitives(registry, out string missing));
            Assert.AreEqual("SelectSellableItem", missing);
        }

        [Test]
        public void UtilityFallbackUsesTieOrderAndIdleWhenLow()
        {
            var state = new AIPersonalState("ai-1");
            state.UpdateNeeds(new AIPersonalStateInput(0f, 48, 24, 1, 0, 100, 0f, 10f));
            var fallback = new UtilityFallback();
            var catalog = new ActionTemplateCatalog(ActionTemplateDefinition.CreateM4Defaults());

            Assert.AreEqual(FallbackNeed.Food, fallback.SelectNeed(state));
            Assert.AreEqual("survival.eat_owned_food", fallback.SelectTemplate(state, catalog).TemplateId);

            state.UpdateNeeds(new AIPersonalStateInput(100f, 48, 24, 1, 0, 100, 0f, 10f));
            Assert.AreEqual(FallbackNeed.Cleanup, fallback.SelectNeed(state));

            state.UpdateNeeds(new AIPersonalStateInput(100f, 0, 24, 0, 100, 100, 0f, 10f));
            Assert.AreEqual(FallbackNeed.Idle, fallback.SelectNeed(state));
            Assert.AreEqual("safety.idle_at_camp", fallback.SelectTemplate(state, catalog).TemplateId);
        }


        [Test]
        public void DecisionRequestJsonIncludesServerIdAndSnakeCase()
        {
            var request = new DecisionRequest
            {
                ActorId = "ai-1",
                WorldId = "world-1",
                Reason = "cleanup"
            };
            request.StateVersions["personal_state"] = 7L;

            string json = AIDecisionJsonCodec.FormatDecisionRequest("server-1", request);

            StringAssert.Contains("\"actor_id\":\"ai-1\"", json);
            StringAssert.Contains("\"world_id\":\"world-1\"", json);
            StringAssert.Contains("\"server_id\":\"server-1\"", json);
            StringAssert.Contains("\"state_versions\":{\"personal_state\":7}", json);
        }

        [Test]
        public void ActorStateSavePayloadIncludesWorldId()
        {
            InMemoryItemDefinitionCatalog catalog = M3ItemDefinitions.CreateCatalog();
            var inventoryService = new InventoryService(catalog);
            var owner = new InventoryOwner("ai", "ai-1");
            var adapter = new AIInventoryAdapter(inventoryService, owner, catalog);
            var actor = new AIActorController("ai-1", adapter, PrimitiveActionRegistry.CreateM4Defaults());
            var persistence = new AIActorRuntimeStatePersistence(NullActorStateGateway.Instance, "world-1");

            SaveRequest request = persistence.CreateSaveRequest(actor);
            string personalState = request.PersonalState.ToStringUtf8();

            StringAssert.Contains("\"actor_id\":\"ai-1\"", personalState);
            StringAssert.Contains("\"world_id\":\"world-1\"", personalState);
        }

        [Test]
        public void DecisionApplyIsIdempotentForDuplicateDecisionId()
        {
            InMemoryItemDefinitionCatalog catalog = M3ItemDefinitions.CreateCatalog();
            var inventoryService = new InventoryService(catalog);
            var owner = new InventoryOwner("ai", "ai-1");
            var adapter = new AIInventoryAdapter(inventoryService, owner, catalog);
            PrimitiveActionRegistry registry = PrimitiveActionRegistry.CreateM4Defaults();
            var actor = new AIActorController("ai-1", adapter, registry);
            var templates = new ActionTemplateCatalog(ActionTemplateDefinition.CreateM4Defaults());
            var client = new AIDecisionClient("server-1", "world-1", templates, NullAIDecisionTransport.Instance);
            long decisionStateVersion = actor.PersonalState.Version;
            var decision = new ActionDecision
            {
                DecisionId = "decision-1",
                ActorId = "ai-1",
                StateVersion = decisionStateVersion,
                TemplateId = "safety.idle_at_camp",
                CreatedAtUnixMs = 1000L
            };

            DecisionApplicationResult first = client.ApplyDecision(decision, actor, 1000L);
            long versionAfterFirst = actor.PersonalState.Version;
            DecisionApplicationResult duplicate = client.ApplyDecision(decision, actor, 1000L);

            Assert.AreEqual(DecisionApplicationStatus.Applied, first.Status);
            Assert.AreEqual(DecisionApplicationStatus.Duplicate, duplicate.Status);
            Assert.AreEqual(versionAfterFirst, actor.PersonalState.Version);
            Assert.AreEqual("safety.idle_at_camp", actor.PersonalState.ActionState.ActiveTemplateId);
        }

        [Test]
        public void DecisionTemplateIdStepUsesCatalogTemplateSteps()
        {
            InMemoryItemDefinitionCatalog catalog = M3ItemDefinitions.CreateCatalog();
            var inventoryService = new InventoryService(catalog);
            var owner = new InventoryOwner("ai", "ai-1");
            var adapter = new AIInventoryAdapter(inventoryService, owner, catalog);
            var actor = new AIActorController("ai-1", adapter, PrimitiveActionRegistry.CreateM4Defaults());
            var templates = new ActionTemplateCatalog(ActionTemplateDefinition.CreateM4Defaults());
            var client = new AIDecisionClient("server-1", "world-1", templates, NullAIDecisionTransport.Instance);
            client.PublishRequest(actor, "test");
            var decision = new ActionDecision
            {
                DecisionId = "decision-template-step",
                ActorId = "ai-1",
                StateVersion = actor.PersonalState.Version,
                TemplateId = "safety.idle_at_camp",
                CreatedAtUnixMs = 1000L
            };
            decision.Steps.Add(new ActionStep { ActionTemplateId = "safety.idle_at_camp" });

            DecisionApplicationResult result = client.ApplyDecision(decision, actor, 1000L);

            Assert.AreEqual(DecisionApplicationStatus.Applied, result.Status);
            Assert.AreEqual("safety.idle_at_camp", actor.PersonalState.ActionState.ActiveTemplateId);
        }
        [Test]
        public void RunnerCancelExecutesRegisteredCompensation()
        {
            int released = 0;
            var registry = new PrimitiveActionRegistry();
            registry.Register("Reserve", context =>
            {
                context.RegisterCompensation(() => released++);
                return PrimitiveActionResult.Running();
            });
            var template = new ActionTemplateDefinition("test.reserve", 1, new[] { "test" }, null, null, new[] { new ActionStepSpec("Reserve", null) }, 0, 60, 0);
            var state = new AIPersonalState("ai-1");
            var runner = new ActionTemplateRunner();

            ActionTemplateStartResult start = runner.Start(template, registry, new AIPreconditionContext(state, new AIInventorySummary(0, 24, 24, 0, 0)), 0L);
            ActionTemplateTickResult tick = runner.Tick(registry, new PrimitiveActionContext("ai-1", state, null, null, null, 100L, 0.1f));
            runner.Cancel("switch", 200L);

            Assert.IsTrue(start.Success);
            Assert.AreEqual(RunnerTickStatus.Running, tick.Status);
            Assert.AreEqual(1, released);
        }

        [Test]
        public void AIActorSystemSpawnsTwentyActorsAndTimeSlicesTicks()
        {
            AIActorSystem system = AIActorSystem.CreateDefault("server-1", "world-1", M3ItemDefinitions.CreateCatalog(), NullActorStateGateway.Instance);

            int processed = system.Tick(1000L, 0.05f, 5);

            Assert.AreEqual(AIActorSystem.DefaultActorCount, system.Actors.Count);
            Assert.AreEqual(5, processed);
        }
    }
}

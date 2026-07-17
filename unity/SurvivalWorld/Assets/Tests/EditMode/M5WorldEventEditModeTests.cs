using System.Collections.Generic;
using Google.Protobuf;
using NUnit.Framework;
using Survival.V1;
using SurvivalWorld.Inventory;
using SurvivalWorld.Items;
using SurvivalWorld.Server.AI;
using SurvivalWorld.Server.WorldEvents;
using SurvivalWorld.Shared.MasterData;

namespace SurvivalWorld.Tests
{
    public sealed class M5WorldEventEditModeTests
    {
        [Test]
        public void DecisionApplyRejectsUnknownPrimitiveStepWithoutSideEffects()
        {
            AIActorController actor = CreateActor("ai-1", PrimitiveActionRegistry.CreateM4Defaults());
            var templates = new ActionTemplateCatalog(ActionTemplateDefinition.CreateM4Defaults());
            var client = new AIDecisionClient("server-1", "world-1", templates, NullAIDecisionTransport.Instance);
            client.PublishRequest(actor, "test");
            var decision = new ActionDecision
            {
                DecisionId = "decision-unknown-primitive",
                ActorId = "ai-1",
                StateVersion = actor.PersonalState.Version,
                TemplateId = "safety.idle_at_camp",
                CreatedAtUnixMs = 1000L
            };
            decision.Steps.Add(new ActionStep { ActionTemplateId = "NotRegisteredPrimitive" });

            DecisionApplicationResult result = client.ApplyDecision(decision, actor, 1000L);

            Assert.AreEqual(DecisionApplicationStatus.Rejected, result.Status);
            Assert.IsFalse(actor.Runner.HasActiveTemplate);
            StringAssert.Contains("Unknown primitive", result.Error);
        }

        [Test]
        public void DecisionApplyExecutesAllowedPrimitiveStepParameters()
        {
            string observedTarget = string.Empty;
            var registry = new PrimitiveActionRegistry();
            registry.Register("RecordTarget", context =>
            {
                observedTarget = context.GetParameter("target_ref");
                return PrimitiveActionResult.Completed();
            });
            AIActorController actor = CreateActor("ai-1", registry);
            var template = new ActionTemplateDefinition("test.dynamic", 1, new[] { "test" }, null, null, new ActionStepSpec[0], 0, 60, 0);
            var templates = new ActionTemplateCatalog(new[] { template });
            var client = new AIDecisionClient("server-1", "world-1", templates, NullAIDecisionTransport.Instance, registry);
            client.PublishRequest(actor, "test");
            var decision = new ActionDecision
            {
                DecisionId = "decision-primitive-step",
                ActorId = "ai-1",
                StateVersion = actor.PersonalState.Version,
                TemplateId = "test.dynamic",
                CreatedAtUnixMs = 1000L
            };
            var step = new ActionStep { ActionTemplateId = "RecordTarget" };
            step.Params["target_ref"] = "node-7";
            decision.Steps.Add(step);

            DecisionApplicationResult result = client.ApplyDecision(decision, actor, 1000L);
            ActionTemplateTickResult tick = actor.Tick(1010L, 0.05f);

            Assert.AreEqual(DecisionApplicationStatus.Applied, result.Status);
            Assert.AreEqual(RunnerTickStatus.Completed, tick.Status);
            Assert.AreEqual("node-7", observedTarget);
        }

        [Test]
        public void DecisionApplyRejectsExpiredLeaseWindow()
        {
            AIActorController actor = CreateActor("ai-1", PrimitiveActionRegistry.CreateM4Defaults());
            var templates = new ActionTemplateCatalog(ActionTemplateDefinition.CreateM4Defaults());
            var client = new AIDecisionClient("server-1", "world-1", templates, NullAIDecisionTransport.Instance);
            client.PublishRequest(actor, "test");
            var decision = new ActionDecision
            {
                DecisionId = "decision-expired",
                ActorId = "ai-1",
                StateVersion = actor.PersonalState.Version,
                TemplateId = "safety.idle_at_camp",
                CreatedAtUnixMs = 1000L
            };

            DecisionApplicationResult result = client.ApplyDecision(decision, actor, 62000L);

            Assert.AreEqual(DecisionApplicationStatus.Rejected, result.Status);
            StringAssert.Contains("expired", result.Error);
        }

        [Test]
        public void GreatHuntEffectEnforcesAliveAndTotalCapsThenCompletes()
        {
            var sink = new FakeWorldEventSpawnSink();
            WorldEventInstanceRunner runner = CreateRunner(WorldEventTemplateIds.GreatHunt);
            runner.Activate(0L);

            for (int i = 0; i < 40; i++)
            {
                runner.Tick(i * 1000L, sink);
                sink.HarvestRareDeer(5);
                runner.RecordHarvested(5);
            }

            Assert.AreEqual(GreatHuntEffect.TotalSpawnCap, runner.Stats.Spawned);
            Assert.LessOrEqual(sink.MaxAliveRareDeer, GreatHuntEffect.AliveCapIncrease);
            Assert.AreEqual(Survival.V1.WorldEventState.Active, runner.State);

            bool completed = runner.Tick(GreatHuntEffect.DurationSeconds * 1000L, sink);

            Assert.IsTrue(completed);
            Assert.AreEqual(Survival.V1.WorldEventState.Completed, runner.State);
        }

        [Test]
        public void RareResourceEffectEnforcesNodeAndYieldBudgetCaps()
        {
            var sink = new FakeWorldEventSpawnSink();
            var parameters = WorldEventParameterBag.From(ByteString.CopyFromUtf8("{\"total_yield_budget\":50,\"yield_per_node\":10}"));
            var config = new WorldEventInstanceConfig("event-1", "proposal-1", WorldEventTemplateIds.RareResource, "world-1", "region-1", parameters);
            var runner = new WorldEventInstanceRunner(config, WorldEventEffectFactory.Create(WorldEventTemplateIds.RareResource, parameters));
            runner.Activate(0L);

            for (int i = 0; i < 20; i++)
            {
                runner.Tick(i * 1000L, sink);
            }

            Assert.AreEqual(5, runner.Stats.Spawned);
            Assert.AreEqual(50, sink.TotalOreYieldBudget);
            Assert.LessOrEqual(sink.ActiveRareOreNodes, RareResourceEffect.NodeCap);
        }

        [Test]
        public void RareBuyerRushSpawnsThreeIndependentBuyersAndCompletesAtTenMinutes()
        {
            var sink = new FakeWorldEventSpawnSink();
            WorldEventInstanceRunner runner = CreateRunner(WorldEventTemplateIds.RareBuyerRush);
            runner.Activate(0L);

            runner.Tick(1000L, sink);

            Assert.AreEqual(RareBuyerRushEffect.BuyerCount, runner.Stats.Spawned);
            Assert.AreEqual(RareBuyerRushEffect.BuyerCount, sink.ActiveRareBuyers);
            CollectionAssert.AllItemsAreUnique(sink.BuyerSeeds);

            bool completed = runner.Tick(RareBuyerRushEffect.DurationSeconds * 1000L, sink);

            Assert.IsTrue(completed);
            Assert.AreEqual(Survival.V1.WorldEventState.Completed, runner.State);
        }

        [Test]
        public void DirectorApprovesRejectsConflictAndCompletesWithStatsUpdate()
        {
            var gateway = new FakeWorldEventGateway();
            var sink = new FakeWorldEventSpawnSink();
            var director = new WorldEventDirectorClient("world-1", gateway, sink);
            EventProposal first = Proposal("proposal-1", WorldEventTemplateIds.RareBuyerRush, "region-a");
            EventProposal conflicting = Proposal("proposal-2", WorldEventTemplateIds.GreatHunt, "region-a");

            WorldEventProposalResult approved = director.HandleProposal(first, 0L);
            WorldEventProposalResult rejected = director.HandleProposal(conflicting, 0L);
            int completed = director.Tick(RareBuyerRushEffect.DurationSeconds * 1000L);

            Assert.AreEqual(WorldEventProposalStatus.Approved, approved.Status);
            Assert.AreEqual(WorldEventProposalStatus.Rejected, rejected.Status);
            Assert.AreEqual("region_conflict", rejected.ReasonCode);
            Assert.AreEqual(1, completed);
            Assert.AreEqual(1, gateway.RegisterRequests.Count);
            Assert.AreEqual(2, gateway.UpdateRequests.Count);
            Assert.AreEqual(Survival.V1.WorldEventState.Active, gateway.UpdateRequests[0].NewState);
            Assert.AreEqual(Survival.V1.WorldEventState.Completed, gateway.UpdateRequests[1].NewState);
            StringAssert.Contains("spawned", gateway.UpdateRequests[1].Stats.ToStringUtf8());
        }

        [Test]
        public void DirectorRejectsVersionMismatchFromProposalParams()
        {
            var director = new WorldEventDirectorClient("world-1", new FakeWorldEventGateway(), new FakeWorldEventSpawnSink());
            EventProposal proposal = Proposal("proposal-version", WorldEventTemplateIds.GreatHunt, "region-a");
            proposal.Params = ByteString.CopyFromUtf8("{\"rules_version\":999}");

            WorldEventProposalResult result = director.HandleProposal(proposal, 0L);

            Assert.AreEqual(WorldEventProposalStatus.Rejected, result.Status);
            Assert.AreEqual("version_mismatch", result.ReasonCode);
        }

        private static AIActorController CreateActor(string actorId, PrimitiveActionRegistry registry)
        {
            InMemoryItemDefinitionCatalog catalog = M3ItemDefinitions.CreateCatalog();
            var inventoryService = new InventoryService(catalog);
            var owner = new InventoryOwner("ai", actorId);
            var adapter = new AIInventoryAdapter(inventoryService, owner, catalog);
            return new AIActorController(actorId, adapter, registry);
        }

        private static WorldEventInstanceRunner CreateRunner(string templateId)
        {
            var parameters = WorldEventParameterBag.Empty;
            var config = new WorldEventInstanceConfig("event-1", "proposal-1", templateId, "world-1", "region-1", parameters);
            return new WorldEventInstanceRunner(config, WorldEventEffectFactory.Create(templateId, parameters));
        }

        private static EventProposal Proposal(string proposalId, string templateId, string regionId)
        {
            return new EventProposal
            {
                ProposalId = proposalId,
                TemplateId = templateId,
                WorldId = "world-1",
                RegionId = regionId,
                Params = ByteString.CopyFromUtf8("{\"rules_version\":1}"),
                Score = 0.9d
            };
        }

        private sealed class FakeWorldEventSpawnSink : IWorldEventSpawnSink
        {
            public int AliveRareDeer { get; private set; }
            public int ActiveRareOreNodes { get; private set; }
            public int ActiveRareBuyers { get; private set; }
            public int MaxAliveRareDeer { get; private set; }
            public int TotalOreYieldBudget { get; private set; }
            public List<int> BuyerSeeds { get; } = new List<int>();

            public bool SpawnRareDeer(string eventInstanceId, string regionId)
            {
                AliveRareDeer++;
                if (AliveRareDeer > MaxAliveRareDeer)
                {
                    MaxAliveRareDeer = AliveRareDeer;
                }

                return true;
            }

            public bool SpawnRareOreNode(string eventInstanceId, string regionId, int yieldBudget)
            {
                ActiveRareOreNodes++;
                TotalOreYieldBudget += yieldBudget;
                return true;
            }

            public bool SpawnRareBuyer(string eventInstanceId, string regionId, int inventorySeed)
            {
                ActiveRareBuyers++;
                BuyerSeeds.Add(inventorySeed);
                return true;
            }

            public void HarvestRareDeer(int count)
            {
                AliveRareDeer = System.Math.Max(0, AliveRareDeer - count);
            }
        }

        private sealed class FakeWorldEventGateway : IWorldEventServiceGateway
        {
            public List<RegisterRequest> RegisterRequests { get; } = new List<RegisterRequest>();
            public List<UpdateStateRequest> UpdateRequests { get; } = new List<UpdateStateRequest>();

            public RegisterResponse Register(RegisterRequest request)
            {
                RegisterRequests.Add(request);
                return new RegisterResponse { EventInstanceId = "event-" + RegisterRequests.Count };
            }

            public UpdateStateResponse UpdateState(UpdateStateRequest request)
            {
                UpdateRequests.Add(request);
                return new UpdateStateResponse { Status = ResultStatus.Ok };
            }
        }
    }
}
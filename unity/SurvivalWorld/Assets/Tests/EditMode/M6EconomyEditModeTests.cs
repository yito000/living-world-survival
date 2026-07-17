using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Survival.V1;
using SurvivalWorld.Economy;
using SurvivalWorld.Inventory;
using SurvivalWorld.Items;
using SurvivalWorld.Server.AI;
using SurvivalWorld.Server.AI.Economy;
using UnityEngine;
using EconomyBuyerStockEntry = SurvivalWorld.Economy.BuyerStockEntry;
using RuntimeInventoryEntry = SurvivalWorld.Inventory.InventoryEntry;

namespace SurvivalWorld.Tests
{
    public sealed class M6EconomyEditModeTests
    {
        [Test]
        public async Task PurchaseCommitReflectsApiGrantWithoutOutboxAndIsIdempotent()
        {
            var sink = new CapturingInventoryEventSink();
            var runtime = new InventoryRuntimeService(ItemDefinitionCatalog.CreateMvpCatalog(), sink, "world-m6");
            var registry = new BuyerRegistry();
            var buyer = CreateBuyer("buyer-1", BuyerStatus.Active, Vector3.zero, 2);
            registry.AddOrReplace(buyer);
            var fakeEconomy = new FakeEconomyClient(new CommitPurchaseResponse
            {
                Status = PurchaseStatus.Committed,
                NewPersistedInventoryVersion = 1,
                Charged = new Money { Amount = 25 }
            });
            fakeEconomy.PurchaseResponse.GrantedItems.Add(new ItemRef { ItemDefinitionId = "stone" });
            var protocol = CreateProtocol(fakeEconomy, runtime);
            var handler = new BuyerPurchaseHandler(registry, runtime, protocol, "world-m6");
            var actor = new BuyerPurchaseActor("player", "player-1", Vector3.zero, true);
            var command = new BuyerPurchaseCommand { CommandId = "cmd-buy", BuyerInstanceId = "buyer-1", StockEntryId = "stock-1", InventoryVersion = 0 };

            BuyerPurchaseResult first = await handler.HandleAsync(actor, command, CancellationToken.None);
            BuyerPurchaseResult duplicate = await handler.HandleAsync(actor, command, CancellationToken.None);
            InventorySnapshot snapshot = runtime.RequestSnapshot("player", "player-1");

            Assert.AreEqual(BuyerPurchaseResultStatus.Committed, first.Status);
            Assert.AreEqual(BuyerPurchaseResultStatus.Committed, duplicate.Status);
            Assert.AreEqual(1, fakeEconomy.CommitPurchaseCalls);
            Assert.AreEqual(0, sink.Events.Count, "API-confirmed purchase grants must not enter the normal DS outbox.");
            Assert.AreEqual(1, snapshot.Version);
            Assert.AreEqual(1, snapshot.Entries.Count);
            Assert.AreEqual("stone", snapshot.Entries[0].ItemDefinitionId);
            Assert.AreEqual(1, snapshot.Entries[0].Quantity);
            Assert.AreEqual(1, buyer.StockEntry("stock-1").RemainingQuantity);
        }

        [Test]
        public async Task PurchaseValidationRejectsDistanceInactiveVersionAndEmptyStockBeforeApi()
        {
            var runtime = new InventoryRuntimeService(ItemDefinitionCatalog.CreateMvpCatalog(), NullInventoryEventSink.Instance, "world-m6");
            var registry = new BuyerRegistry();
            var buyer = CreateBuyer("buyer-1", BuyerStatus.Active, Vector3.zero, 1);
            registry.AddOrReplace(buyer);
            var fakeEconomy = new FakeEconomyClient(new CommitPurchaseResponse { Status = PurchaseStatus.Committed });
            var handler = new BuyerPurchaseHandler(registry, runtime, CreateProtocol(fakeEconomy, runtime), "world-m6", 2f);

            BuyerPurchaseResult far = await handler.HandleAsync(
                new BuyerPurchaseActor("player", "player-far", new Vector3(10f, 0f, 0f), true),
                new BuyerPurchaseCommand { CommandId = "cmd-far", BuyerInstanceId = "buyer-1", StockEntryId = "stock-1", InventoryVersion = 0 },
                CancellationToken.None);

            BuyerPurchaseResult conflict = await handler.HandleAsync(
                new BuyerPurchaseActor("player", "player-conflict", Vector3.zero, true),
                new BuyerPurchaseCommand { CommandId = "cmd-conflict", BuyerInstanceId = "buyer-1", StockEntryId = "stock-1", InventoryVersion = 99 },
                CancellationToken.None);

            buyer.BeginPreparing();
            BuyerPurchaseResult inactive = await handler.HandleAsync(
                new BuyerPurchaseActor("player", "player-inactive", Vector3.zero, true),
                new BuyerPurchaseCommand { CommandId = "cmd-inactive", BuyerInstanceId = "buyer-1", StockEntryId = "stock-1", InventoryVersion = 0 },
                CancellationToken.None);

            var emptyBuyer = CreateBuyer("buyer-empty", BuyerStatus.Active, Vector3.zero, 0);
            registry.AddOrReplace(emptyBuyer);
            BuyerPurchaseResult outOfStock = await handler.HandleAsync(
                new BuyerPurchaseActor("player", "player-empty", Vector3.zero, true),
                new BuyerPurchaseCommand { CommandId = "cmd-empty", BuyerInstanceId = "buyer-empty", StockEntryId = "stock-1", InventoryVersion = 0 },
                CancellationToken.None);

            Assert.AreEqual(BuyerPurchaseResultStatus.Rejected, far.Status);
            Assert.AreEqual(BuyerPurchaseResultStatus.Conflict, conflict.Status);
            Assert.AreEqual(BuyerPurchaseResultStatus.Rejected, inactive.Status);
            Assert.AreEqual(BuyerPurchaseResultStatus.OutOfStock, outOfStock.Status);
            Assert.AreEqual(0, fakeEconomy.CommitPurchaseCalls);
        }

        [Test]
        public async Task PersistedVersionMismatchRequestsFullSnapshotAndAppliesItWhenAvailable()
        {
            var runtime = new InventoryRuntimeService(ItemDefinitionCatalog.CreateMvpCatalog(), NullInventoryEventSink.Instance, "world-m6");
            var fakeEconomy = new FakeEconomyClient(new CommitPurchaseResponse
            {
                Status = PurchaseStatus.Committed,
                NewPersistedInventoryVersion = 3
            });
            fakeEconomy.PurchaseResponse.GrantedItems.Add(new ItemRef { ItemDefinitionId = "stone" });
            var snapshotProvider = new CapturingSnapshotProvider(new InventorySnapshot("player", "player-1", 3, new[] { new RuntimeInventoryEntry(0, "wood", string.Empty, 4, 0) }));
            var protocol = new PurchaseProtocol(fakeEconomy, runtime, new InventoryReconciler(runtime, snapshotProvider));

            BuyerPurchaseResult result = await protocol.CommitPurchaseAsync(new BuyerPurchaseContext
            {
                WorldId = "world-m6",
                OwnerType = "player",
                PurchaserId = "player-1",
                CommandId = "cmd-resync",
                BuyerInstanceId = "buyer-1",
                StockEntryId = "stock-1",
                InventoryVersion = 0
            }, CancellationToken.None);
            InventorySnapshot snapshot = runtime.RequestSnapshot("player", "player-1");

            Assert.AreEqual(BuyerPurchaseResultStatus.Committed, result.Status);
            Assert.IsTrue(result.ResyncRequested);
            Assert.IsTrue(result.SnapshotApplied);
            Assert.AreEqual(1, snapshotProvider.LastRequest.LastKnownVersion);
            Assert.AreEqual(3, snapshot.Version);
            Assert.AreEqual("wood", snapshot.Entries[0].ItemDefinitionId);
        }

        [Test]
        public void PurchasePrimitiveUsesBuyerPurchaseHandlerForAi()
        {
            var runtime = new InventoryRuntimeService(ItemDefinitionCatalog.CreateMvpCatalog(), NullInventoryEventSink.Instance, "world-m6");
            var registry = new BuyerRegistry();
            registry.AddOrReplace(CreateBuyer("buyer-ai", BuyerStatus.Active, Vector3.zero, 1));
            var fakeEconomy = new FakeEconomyClient(new CommitPurchaseResponse
            {
                Status = PurchaseStatus.Committed,
                NewPersistedInventoryVersion = 1
            });
            fakeEconomy.PurchaseResponse.GrantedItems.Add(new ItemRef { ItemDefinitionId = "cooked_meat" });
            var handler = new BuyerPurchaseHandler(registry, runtime, CreateProtocol(fakeEconomy, runtime), "world-m6");
            PrimitiveActionRegistry primitiveRegistry = PrimitiveActionRegistry.CreateM4Defaults();
            PurchasePrimitive.Register(primitiveRegistry, handler);
            var adapter = new AIInventoryAdapter(runtime, "ai", "ai-1", ItemDefinitionCatalog.CreateMvpCatalog());
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["buyer_instance_id"] = "buyer-ai",
                ["stock_entry_id"] = "stock-1",
                ["actor_x"] = "0",
                ["actor_y"] = "0",
                ["actor_z"] = "0"
            };
            var context = new PrimitiveActionContext("ai-1", new AIPersonalState("ai-1"), adapter, new Dictionary<string, string>(StringComparer.Ordinal), null, 1000L, 0.05f);

            PrimitiveActionResult result = primitiveRegistry.Execute(new ActionStepSpec(PurchasePrimitive.PurchaseStub, parameters), context);
            InventorySnapshot snapshot = runtime.RequestSnapshot("ai", "ai-1");

            Assert.AreEqual(PrimitiveActionStatus.Completed, result.Status);
            Assert.AreEqual(1, fakeEconomy.CommitPurchaseCalls);
            Assert.AreEqual(1, snapshot.Version);
            Assert.AreEqual("cooked_meat", snapshot.Entries[0].ItemDefinitionId);
        }

        [Test]
        public void SpawnPlanUsesDeterministicSeedAndThirtyMinuteWindow()
        {
            var controller = new BuyerSpawnController(NullEconomyClient.Instance, new BuyerRegistry(), "world-m6", "region-a", "mvp-stock", 10000);

            BuyerSpawnPlan first = controller.CreatePlan(1000L, 7);
            BuyerSpawnPlan second = controller.CreatePlan(1000L, 7);

            Assert.AreEqual(first.Seed, second.Seed);
            Assert.AreEqual(first.SpawnAtUnixMs, second.SpawnAtUnixMs);
            Assert.GreaterOrEqual(first.SpawnAtUnixMs - 1000L, BuyerSpawnController.BaseSpawnIntervalMs - BuyerSpawnController.SpawnJitterMs);
            Assert.LessOrEqual(first.SpawnAtUnixMs - 1000L, BuyerSpawnController.BaseSpawnIntervalMs + BuyerSpawnController.SpawnJitterMs);
            Assert.AreEqual(BuyerSpawnController.StayDurationMs, first.DespawnAtUnixMs - first.SpawnAtUnixMs);
        }

        private static PurchaseProtocol CreateProtocol(IEconomyClient economyClient, InventoryRuntimeService runtime)
        {
            return new PurchaseProtocol(economyClient, runtime, new InventoryReconciler(runtime, NullInventorySnapshotProvider.Instance));
        }

        private static BuyerInstance CreateBuyer(string buyerId, BuyerStatus status, Vector3 position, int quantity)
        {
            var buyer = new BuyerInstance(
                buyerId,
                "region-a",
                position,
                0L,
                BuyerSpawnController.StayDurationMs,
                new[] { new EconomyBuyerStockEntry("stock-1", "stone", 25L, quantity) });

            if (status == BuyerStatus.Active)
            {
                buyer.MarkActive();
            }
            else if (status == BuyerStatus.Preparing)
            {
                buyer.MarkActive();
                buyer.BeginPreparing();
            }
            else if (status == BuyerStatus.Despawned)
            {
                buyer.MarkDespawned();
            }

            return buyer;
        }

        private sealed class FakeEconomyClient : IEconomyClient
        {
            public FakeEconomyClient(CommitPurchaseResponse purchaseResponse)
            {
                PurchaseResponse = purchaseResponse;
            }

            public CommitPurchaseResponse PurchaseResponse { get; }
            public int CommitPurchaseCalls { get; private set; }

            public Task<RegisterBuyerResult> RegisterBuyerAsync(RegisterBuyerRequestData request, CancellationToken cancellationToken)
            {
                return Task.FromResult(RegisterBuyerResult.Ok("buyer-registered", new[] { new EconomyBuyerStockEntry("stock-1", "stone", 25L, 1) }));
            }

            public Task<CommitPurchaseResponse> CommitPurchaseAsync(CommitPurchaseRequest request, CancellationToken cancellationToken)
            {
                CommitPurchaseCalls++;
                return Task.FromResult(PurchaseResponse);
            }

            public Task<CommitSaleResponse> CommitSaleAsync(CommitSaleRequest request, CancellationToken cancellationToken)
            {
                return Task.FromResult(new CommitSaleResponse { Status = ResultStatus.Ok });
            }

            public Task<BuyerDespawnResult> DespawnBuyerAsync(string buyerInstanceId, BuyerStatus targetStatus, CancellationToken cancellationToken)
            {
                return Task.FromResult(BuyerDespawnResult.Ok());
            }
        }

        private sealed class CapturingSnapshotProvider : IInventorySnapshotProvider
        {
            private readonly InventorySnapshot snapshot;

            public CapturingSnapshotProvider(InventorySnapshot snapshot)
            {
                this.snapshot = snapshot;
            }

            public RequestInventorySnapshot LastRequest { get; private set; }

            public InventorySnapshot RequestFullSnapshot(string ownerType, string ownerId, RequestInventorySnapshot request)
            {
                LastRequest = request;
                return snapshot;
            }
        }

        private sealed class CapturingInventoryEventSink : IInventoryEventSink
        {
            public readonly List<DomainEvent> Events = new List<DomainEvent>();

            public void Enqueue(DomainEvent domainEvent)
            {
                Events.Add(domainEvent);
            }
        }
    }

    internal static class BuyerInstanceTestExtensions
    {
        public static EconomyBuyerStockEntry StockEntry(this BuyerInstance buyer, string stockEntryId)
        {
            Assert.IsTrue(buyer.TryGetStock(stockEntryId, out EconomyBuyerStockEntry stock));
            return stock;
        }
    }
}

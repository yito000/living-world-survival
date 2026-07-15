using System.Collections.Generic;
using NUnit.Framework;
using Survival.V1;
using SurvivalWorld.Inventory;
using SurvivalWorld.Items;

namespace SurvivalWorld.Tests
{
    public sealed class M2InventoryServiceTests
    {
        [Test]
        public void AddItemStacksWithinLimitAndIncrementsVersion()
        {
            InventoryService service = new InventoryService(ItemDefinitionCatalog.CreateMvpCatalog());
            InventoryOwner owner = new InventoryOwner("player", "player-1", 24, 100f, 0);

            InventoryMutationResult result = service.AddItem(owner, "stone", string.Empty, 55);

            Assert.IsTrue(result.Success);
            Assert.AreEqual(1, owner.Version);
            Assert.AreEqual(2, owner.Entries.Count);
            Assert.AreEqual(50, owner.Entries[0].Quantity);
            Assert.AreEqual(5, owner.Entries[1].Quantity);
        }

        [Test]
        public void AddItemRejectsCapacityWithoutChangingVersion()
        {
            InventoryService service = new InventoryService(ItemDefinitionCatalog.CreateMvpCatalog());
            InventoryOwner owner = new InventoryOwner("player", "player-1", 1, 40f, 0);

            InventoryMutationResult result = service.AddItem(owner, "stone", string.Empty, 51);

            Assert.AreEqual(InventoryMutationStatus.Rejected, result.Status);
            Assert.AreEqual(0, owner.Version);
            Assert.AreEqual(0, owner.Entries.Count);
        }

        [Test]
        public void ApplyCommandRejectsVersionConflictAndReturnsSnapshot()
        {
            InventoryService service = new InventoryService(ItemDefinitionCatalog.CreateMvpCatalog());
            InventoryOwner owner = new InventoryOwner("player", "player-1", 24, 100f, 0);
            service.AddItem(owner, "wood", string.Empty, 2);

            InventoryMutationResult result = service.ApplyCommand(owner, new InventoryCommand
            {
                CommandId = "cmd-conflict",
                ExpectedVersion = 0,
                Operation = InventoryOperation.Drop,
                ItemRef = new ItemRef { ItemDefinitionId = "wood" },
                Quantity = 1
            });

            Assert.AreEqual(InventoryMutationStatus.Conflict, result.Status);
            Assert.AreEqual(1, owner.Version);
            Assert.AreEqual(2, owner.Entries[0].Quantity);
            Assert.AreEqual(1, result.Snapshot.Version);
        }

        [Test]
        public void ApplyCommandIsIdempotentByCommandId()
        {
            InventoryService service = new InventoryService(ItemDefinitionCatalog.CreateMvpCatalog());
            InventoryOwner owner = new InventoryOwner("player", "player-1", 24, 100f, 0);
            service.AddItem(owner, "wood", string.Empty, 3);
            long expectedVersion = owner.Version;
            var command = new InventoryCommand
            {
                CommandId = "cmd-drop",
                ExpectedVersion = expectedVersion,
                Operation = InventoryOperation.Drop,
                ItemRef = new ItemRef { ItemDefinitionId = "wood" },
                Quantity = 1
            };

            InventoryMutationResult first = service.ApplyCommand(owner, command);
            InventoryMutationResult second = service.ApplyCommand(owner, command);

            Assert.AreEqual(InventoryMutationStatus.Ok, first.Status);
            Assert.AreEqual(InventoryMutationStatus.Duplicate, second.Status);
            Assert.AreEqual(expectedVersion + 1, owner.Version);
            Assert.AreEqual(2, owner.Entries[0].Quantity);
        }

        [Test]
        public void AddItemCommandIsIdempotentAndEmitsAppendableEvent()
        {
            var sink = new CapturingInventoryEventSink();
            InventoryService service = new InventoryService(ItemDefinitionCatalog.CreateMvpCatalog(), sink, "world-m2");
            InventoryOwner owner = new InventoryOwner("player", "player-1", 24, 100f, 0);

            InventoryMutationResult first = service.AddItemCommand(owner, "cmd-add", 0, "stone", string.Empty, 5);
            InventoryMutationResult second = service.AddItemCommand(owner, "cmd-add", 0, "stone", string.Empty, 5);

            Assert.AreEqual(InventoryMutationStatus.Ok, first.Status);
            Assert.AreEqual(InventoryMutationStatus.Duplicate, second.Status);
            Assert.AreEqual(1, owner.Version);
            Assert.AreEqual(1, owner.Entries.Count);
            Assert.AreEqual(5, owner.Entries[0].Quantity);
            Assert.IsNotNull(first.DomainEvent);
            Assert.AreEqual("inventory.item_added", first.DomainEvent.Type);
            Assert.AreEqual("world-m2", first.DomainEvent.WorldId);
            Assert.AreEqual("player-1", first.DomainEvent.AggregateId);
            Assert.AreEqual(1, first.DomainEvent.LocalSequence);
            Assert.AreEqual(1, sink.Events.Count);
            Assert.AreEqual(first.DomainEvent.EventId, sink.Events[0].EventId);
        }

        [Test]
        public void InventoryRuntimeServiceAppliesSmokeSequenceWithCurrentVersion()
        {
            var sink = new CapturingInventoryEventSink();
            var runtime = new InventoryRuntimeService(ItemDefinitionCatalog.CreateMvpCatalog(), sink, "world-m2");

            InventoryMutationResult add = runtime.AddItemCommand("player", "connection:1", "cmd-add", -1, "stone", string.Empty, 5);
            InventoryMutationResult move = runtime.ApplyCommand("player", "connection:1", new InventoryCommand
            {
                CommandId = "cmd-move",
                ExpectedVersion = -1,
                Operation = InventoryOperation.Move,
                ItemRef = new ItemRef { ItemDefinitionId = "stone" },
                TargetRef = new ItemRef { ItemDefinitionId = "slot:2" }
            });
            InventoryMutationResult drop = runtime.ApplyCommand("player", "connection:1", new InventoryCommand
            {
                CommandId = "cmd-drop",
                ExpectedVersion = -1,
                Operation = InventoryOperation.Drop,
                ItemRef = new ItemRef { ItemDefinitionId = "stone" },
                Quantity = 1
            });

            InventorySnapshot snapshot = runtime.RequestSnapshot("player", "connection:1");

            Assert.AreEqual(InventoryMutationStatus.Ok, add.Status);
            Assert.AreEqual(InventoryMutationStatus.Ok, move.Status);
            Assert.AreEqual(InventoryMutationStatus.Ok, drop.Status);
            Assert.AreEqual(3, snapshot.Version);
            Assert.AreEqual(1, snapshot.Entries.Count);
            Assert.AreEqual(2, snapshot.Entries[0].SlotIndex);
            Assert.AreEqual(4, snapshot.Entries[0].Quantity);
            Assert.AreEqual(3, sink.Events.Count);
            Assert.AreEqual("inventory.item_added", sink.Events[0].Type);
            Assert.AreEqual("inventory.move", sink.Events[1].Type);
            Assert.AreEqual("inventory.drop", sink.Events[2].Type);
        }

        [Test]
        public void UseCommandConsumesUsableFood()
        {
            InventoryService service = new InventoryService(ItemDefinitionCatalog.CreateMvpCatalog());
            InventoryOwner owner = new InventoryOwner("player", "player-1", 24, 100f, 0);
            service.AddItem(owner, "cooked_meat", string.Empty, 2);

            InventoryMutationResult result = service.ApplyCommand(owner, new InventoryCommand
            {
                CommandId = "cmd-use",
                ExpectedVersion = owner.Version,
                Operation = InventoryOperation.Use,
                ItemRef = new ItemRef { ItemDefinitionId = "cooked_meat" },
                Quantity = 1
            });

            Assert.AreEqual(InventoryMutationStatus.Ok, result.Status);
            Assert.AreEqual(1, owner.Entries[0].Quantity);
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
}
using System.Collections.Generic;
using NUnit.Framework;
using Survival.V1;
using SurvivalWorld.Inventory;
using SurvivalWorld.Items;
using SurvivalWorld.Server.Combat;
using SurvivalWorld.Server.Inventory;
using SurvivalWorld.Server.Simulation;
using SurvivalWorld.Shared;
using SurvivalWorld.Shared.Events;
using SurvivalWorld.Shared.MasterData;
using UnityEngine;

namespace SurvivalWorld.Tests
{
    public sealed class M3SurvivalSystemsTests
    {
        [Test]
        public void DamageMatrixAllowsOnlyMvpPairs()
        {
            var service = new DamageService();

            Assert.IsTrue(service.CanDamage(CombatantType.Player, CombatantType.Animal));
            Assert.IsTrue(service.CanDamage(CombatantType.Ai, CombatantType.Animal));
            Assert.IsTrue(service.CanDamage(CombatantType.Animal, CombatantType.Player));
            Assert.IsTrue(service.CanDamage(CombatantType.Animal, CombatantType.Ai));
            Assert.IsFalse(service.CanDamage(CombatantType.Player, CombatantType.Player));
            Assert.IsFalse(service.CanDamage(CombatantType.Player, CombatantType.Ai));
            Assert.IsFalse(service.CanDamage(CombatantType.Ai, CombatantType.Player));
            Assert.IsFalse(service.CanDamage(CombatantType.Ai, CombatantType.Ai));

            var aiTarget = new TestDamageable(CombatantType.Ai, 10f);
            DamageResult rejected = service.ApplyDamage(aiTarget, CombatantType.Player, 5f);
            Assert.IsFalse(rejected.Allowed);
            Assert.AreEqual(0f, rejected.AppliedDamage);
            Assert.AreEqual(10f, aiTarget.Health);
        }

        [Test]
        public void M3RecipesKeepStoneSpearLeatherFreeAndBlueprintGated()
        {
            MasterDataStore store = MasterDataStore.CreateM3Defaults();

            Assert.IsTrue(store.TryGetRecipe("stone_spear", out RecipeDefinition stoneSpear));
            Assert.IsFalse(ContainsIngredient(stoneSpear, "leather"));
            Assert.AreEqual(20, stoneSpear.DurationSeconds);

            Assert.IsTrue(store.TryGetRecipe("iron_hunting_spear", out RecipeDefinition ironSpear));
            Assert.IsTrue(ironSpear.RequiresBlueprint);
            Assert.AreEqual("iron_spear", ironSpear.RequiredBlueprintId);

            Assert.IsTrue(store.TryGetRecipe("cook_raw_meat", out RecipeDefinition cooking));
            Assert.IsTrue(ContainsOutput(cooking, "cooked_meat", 1));
            Assert.IsTrue(ContainsOutput(cooking, "food_waste", 1));
        }

        [Test]
        public void HungerCrossesWarningAndStarvationDrainsHealth()
        {
            var sink = new CapturingSink();
            var catalog = M3ItemDefinitions.CreateCatalog();
            var simulation = new CharacterSimulation(
                new DomainEventFactory("world-m3"),
                sink,
                new M3InventoryService(catalog),
                catalog);
            var state = new CharacterVitalsState("actor-1") { Hunger = 31f, Health = 100f };

            CharacterTickResult warning = simulation.Tick(state, 120f, 1000L);
            Assert.AreEqual(CharacterVitalsBand.Normal, warning.BeforeBand);
            Assert.AreEqual(CharacterVitalsBand.Warning, warning.AfterBand);
            Assert.AreEqual(DomainEventTypes.CharacterVitalsChanged, sink.Events[0].Type);

            state.Hunger = 0f;
            simulation.Tick(state, SurvivalTuning.StarvationHealthDrainIntervalSeconds, 2000L);
            Assert.AreEqual(99f, state.Health);
        }

        [Test]
        public void ResourceMiningDoesNotDecrementNodeWhenInventoryFull()
        {
            var sink = new CapturingSink();
            MasterDataStore masterData = MasterDataStore.CreateM3Defaults();
            var catalog = M3ItemDefinitions.CreateCatalog();
            var inventory = new M3InventoryService(catalog);
            var owner = new InventoryOwner("player", "actor-1", 1, 100f, 0);
            Assert.IsTrue(inventory.Grant(owner, new ItemStack("stone", 50)).Success);

            var system = new ResourceNodeSystem(masterData, inventory, new DomainEventFactory("world-m3"), sink);
            var node = new ResourceNodeState("node-1", "iron", Vector3.zero, 10);

            ResourceMineResult result = system.Mine(
                node,
                owner,
                new ResourceMineRequest(Vector3.zero, 0, true, new[] { "tool.mining" }, 1, 3000L));

            Assert.IsFalse(result.Success);
            Assert.AreEqual(10, node.RemainingAmount);
            Assert.AreEqual(0, node.Version);
            Assert.AreEqual(0, sink.Events.Count);
        }

        [Test]
        public void StationJobReservesAndCancelReleasesIngredients()
        {
            var sink = new CapturingSink();
            MasterDataStore masterData = MasterDataStore.CreateM3Defaults();
            var catalog = M3ItemDefinitions.CreateCatalog();
            var inventory = new M3InventoryService(catalog);
            var owner = new InventoryOwner("player", "actor-1", 24, 100f, 0);
            inventory.Grant(owner, new ItemStack("stone", 3));
            inventory.Grant(owner, new ItemStack("wood", 2));

            var station = new StationState("station-1", "anvil");
            var system = new StationJobSystem(masterData, inventory, new DomainEventFactory("world-m3"), sink);

            StationJobResult started = system.StartJob(station, owner, "actor-1", "stone_spear", 0L);
            Assert.IsTrue(started.Success);
            Assert.AreEqual(3, inventory.CountReserved(owner, "stone"));
            Assert.AreEqual(2, inventory.CountReserved(owner, "wood"));

            StationJobResult cancelled = system.CancelJob(station, owner, 1000L);
            Assert.IsTrue(cancelled.Success);
            Assert.AreEqual(0, inventory.CountReserved(owner, "stone"));
            Assert.AreEqual(0, inventory.CountReserved(owner, "wood"));
            Assert.AreEqual(DomainEventTypes.StationJobStarted, sink.Events[0].Type);
            Assert.AreEqual(DomainEventTypes.StationJobCancelled, sink.Events[1].Type);
        }

        [Test]
        public void HuntingCreatesSingleUseCarcassWithDeterministicDrops()
        {
            var sink = new CapturingSink();
            MasterDataStore masterData = MasterDataStore.CreateM3Defaults();
            var catalog = M3ItemDefinitions.CreateCatalog();
            var inventory = new M3InventoryService(catalog);
            var owner = new InventoryOwner("player", "actor-1", 24, 100f, 0);
            var system = new HuntingSystem(masterData, inventory, new DamageService(), new DomainEventFactory("world-m3"), sink);
            var animal = new AnimalState("animal-1", "deer", 10f);

            HuntingAttackResult attack = system.AttackAnimal("actor-1", CombatantType.Player, animal, 10f, 5000L);
            Assert.IsTrue(attack.Success);
            Assert.IsTrue(attack.Killed);

            M3CommandResult butcher = system.Butcher(attack.CarcassId, owner, 1, 1234, 6000L);
            M3CommandResult duplicate = system.Butcher(attack.CarcassId, owner, 1, 1234, 7000L);

            Assert.IsTrue(butcher.Success);
            Assert.IsFalse(duplicate.Success);
            Assert.AreEqual(1, inventory.CountAvailable(owner, "raw_meat"));
            Assert.AreEqual(1, inventory.CountAvailable(owner, "leather"));
            Assert.AreEqual(1, inventory.CountAvailable(owner, "bone"));
            Assert.AreEqual(DomainEventTypes.HuntingAnimalKilled, sink.Events[0].Type);
            Assert.AreEqual(DomainEventTypes.HuntingCarcassButchered, sink.Events[1].Type);
        }

        private static bool ContainsIngredient(RecipeDefinition recipe, string itemDefinitionId)
        {
            for (int i = 0; i < recipe.Ingredients.Count; i++)
            {
                if (recipe.Ingredients[i].ItemDefinitionId == itemDefinitionId)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsOutput(RecipeDefinition recipe, string itemDefinitionId, int quantity)
        {
            for (int i = 0; i < recipe.Outputs.Count; i++)
            {
                if (recipe.Outputs[i].ItemDefinitionId == itemDefinitionId && recipe.Outputs[i].Quantity == quantity)
                {
                    return true;
                }
            }

            return false;
        }

        private sealed class CapturingSink : IInventoryEventSink
        {
            public readonly List<DomainEvent> Events = new List<DomainEvent>();

            public void Enqueue(DomainEvent domainEvent)
            {
                Events.Add(domainEvent);
            }
        }

        private sealed class TestDamageable : IDamageable
        {
            public TestDamageable(CombatantType combatantType, float health)
            {
                CombatantType = combatantType;
                Health = health;
            }

            public CombatantType CombatantType { get; }
            public float Health { get; private set; }

            public void ApplyDamage(float amount)
            {
                Health -= amount;
            }
        }
    }
}

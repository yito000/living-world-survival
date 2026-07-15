using System;
using System.Collections.Generic;
using UnityEngine;
using SurvivalWorld.Inventory;
using SurvivalWorld.Server.Combat;
using SurvivalWorld.Server.Inventory;
using SurvivalWorld.Shared.Events;
using SurvivalWorld.Shared.MasterData;
using SurvivalWorld.World;

namespace SurvivalWorld.Server.Simulation
{
    public sealed class HuntingSystem
    {
        private readonly MasterDataStore masterData;
        private readonly M3InventoryService inventory;
        private readonly DamageService damageService;
        private readonly DomainEventFactory eventFactory;
        private readonly IInventoryEventSink eventSink;
        private readonly Dictionary<string, CarcassState> carcasses = new Dictionary<string, CarcassState>(StringComparer.Ordinal);

        public HuntingSystem(MasterDataStore masterData, M3InventoryService inventory, DamageService damageService, DomainEventFactory eventFactory, IInventoryEventSink eventSink)
        {
            this.masterData = masterData ?? throw new ArgumentNullException(nameof(masterData));
            this.inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
            this.damageService = damageService ?? new DamageService();
            this.eventFactory = eventFactory ?? throw new ArgumentNullException(nameof(eventFactory));
            this.eventSink = eventSink ?? NullInventoryEventSink.Instance;
        }

        public IReadOnlyDictionary<string, CarcassState> Carcasses => carcasses;

        public HuntingAttackResult AttackAnimal(string attackerId, CombatantType attackerType, AnimalState animal, float damage, long unixTimeMs)
        {
            if (animal == null)
            {
                return HuntingAttackResult.Rejected("Animal is required.");
            }

            DamageResult damageResult = damageService.ApplyDamage(animal, attackerType, damage);
            if (!damageResult.Allowed)
            {
                return HuntingAttackResult.Rejected(damageResult.Error);
            }

            if (!damageResult.Killed)
            {
                return HuntingAttackResult.Hit(damageResult.AppliedDamage);
            }

            string carcassId = DomainEventId.NewUlid(unixTimeMs);
            animal.CarcassId = carcassId;
            var carcass = new CarcassState(carcassId, animal.AnimalId, animal.Species);
            carcasses[carcassId] = carcass;
            eventSink.Enqueue(eventFactory.Create(
                animal.AnimalId,
                DomainEventTypes.HuntingAnimalKilled,
                JsonPayload.Object(
                    JsonPayload.Field("animal_id", animal.AnimalId),
                    JsonPayload.Field("species", animal.Species),
                    JsonPayload.Field("killer_id", attackerId),
                    JsonPayload.Field("carcass_id", carcassId)),
                string.Empty,
                unixTimeMs));
            return HuntingAttackResult.KilledResult(carcassId);
        }

        public M3CommandResult Butcher(string carcassId, InventoryOwner actorInventory, int toolQuality, int dropSeed, long unixTimeMs)
        {
            if (!carcasses.TryGetValue(carcassId ?? string.Empty, out CarcassState carcass))
            {
                return M3CommandResult.Rejected("Carcass not found.");
            }

            if (carcass.Consumed)
            {
                return M3CommandResult.Rejected("Carcass already consumed.");
            }

            IReadOnlyList<ItemStack> drops = ResolveDrops(carcass.Species, toolQuality, dropSeed);
            if (!inventory.CanGrantAll(actorInventory, drops, out string reason))
            {
                return M3CommandResult.Rejected(reason);
            }

            for (int i = 0; i < drops.Count; i++)
            {
                inventory.Grant(actorInventory, drops[i]);
            }

            carcass.Consumed = true;
            eventSink.Enqueue(eventFactory.Create(
                carcass.CarcassId,
                DomainEventTypes.HuntingCarcassButchered,
                JsonPayload.Object(
                    JsonPayload.Field("carcass_id", carcass.CarcassId),
                    JsonPayload.Raw("drops", JsonPayload.ItemStackArray(drops, false)),
                    JsonPayload.Field("drop_seed", dropSeed)),
                string.Empty,
                unixTimeMs));
            return M3CommandResult.Ok();
        }

        public IReadOnlyList<ItemStack> ResolveDrops(string species, int toolQuality, int dropSeed)
        {
            if (!masterData.TryGetDropTable(species, out DropTableDefinition table))
            {
                return Array.Empty<ItemStack>();
            }

            var drops = new List<ItemStack>(table.GuaranteedDrops);
            int rareChance = Math.Min(95, 50 + Math.Max(0, toolQuality) * 10);
            var random = new System.Random(dropSeed);
            for (int i = 0; i < table.RareDrops.Count; i++)
            {
                if (random.Next(0, 100) < rareChance)
                {
                    drops.Add(table.RareDrops[i]);
                }
            }

            return drops;
        }
    }

    public sealed class AnimalState : IDamageable
    {
        public AnimalState(string animalId, string species, float health)
        {
            AnimalId = animalId ?? string.Empty;
            Species = species ?? string.Empty;
            Health = health;
        }

        public string AnimalId { get; }
        public string Species { get; }
        public string CarcassId { get; set; } = string.Empty;
        public CombatantType CombatantType => CombatantType.Animal;
        public float Health { get; private set; }

        public void ApplyDamage(float amount)
        {
            Health = Math.Max(0f, Health - Math.Max(0f, amount));
        }
    }

    public sealed class CarcassState
    {
        public CarcassState(string carcassId, string animalId, string species)
        {
            CarcassId = carcassId ?? string.Empty;
            AnimalId = animalId ?? string.Empty;
            Species = species ?? string.Empty;
        }

        public string CarcassId { get; }
        public string AnimalId { get; }
        public string Species { get; }
        public bool Consumed { get; set; }
    }

    public readonly struct HuntingAttackResult
    {
        private HuntingAttackResult(bool success, bool killed, float appliedDamage, string carcassId, string error)
        {
            Success = success;
            Killed = killed;
            AppliedDamage = appliedDamage;
            CarcassId = carcassId ?? string.Empty;
            Error = error ?? string.Empty;
        }

        public bool Success { get; }
        public bool Killed { get; }
        public float AppliedDamage { get; }
        public string CarcassId { get; }
        public string Error { get; }

        public static HuntingAttackResult Hit(float appliedDamage)
        {
            return new HuntingAttackResult(true, false, appliedDamage, string.Empty, string.Empty);
        }

        public static HuntingAttackResult KilledResult(string carcassId)
        {
            return new HuntingAttackResult(true, true, 0f, carcassId, string.Empty);
        }

        public static HuntingAttackResult Rejected(string error)
        {
            return new HuntingAttackResult(false, false, 0f, string.Empty, error);
        }
    }
}

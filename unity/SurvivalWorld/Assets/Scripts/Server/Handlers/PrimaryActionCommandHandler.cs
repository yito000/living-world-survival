using System;
using Survival.V1;
using SurvivalWorld.Server.Combat;
using SurvivalWorld.Server.Simulation;

namespace SurvivalWorld.Server.Handlers
{
    public sealed class PrimaryActionCommandHandler
    {
        private readonly HuntingSystem huntingSystem;

        public PrimaryActionCommandHandler(HuntingSystem huntingSystem)
        {
            this.huntingSystem = huntingSystem ?? throw new ArgumentNullException(nameof(huntingSystem));
        }

        public HuntingAttackResult Handle(PrimaryActionCommand command, string attackerId, CombatantType attackerType, AnimalState target, long unixTimeMs)
        {
            if (command == null)
            {
                return HuntingAttackResult.Rejected("Command is required.");
            }

            float damage = ResolveServerDamage(command.EquipmentSlot);
            return huntingSystem.AttackAnimal(attackerId, attackerType, target, damage, unixTimeMs);
        }

        private static float ResolveServerDamage(int equipmentSlot)
        {
            return equipmentSlot == 0 ? 35f : 50f;
        }
    }
}

using System;
using UnityEngine;

namespace SurvivalWorld.Server.Combat
{
    public sealed class DamageService
    {
        public bool CanDamage(CombatantType attackerType, CombatantType targetType)
        {
            switch (attackerType)
            {
                case CombatantType.Player:
                    return targetType == CombatantType.Animal;
                case CombatantType.Ai:
                    return targetType == CombatantType.Animal;
                case CombatantType.Animal:
                    return targetType == CombatantType.Player || targetType == CombatantType.Ai;
                default:
                    return false;
            }
        }

        public DamageResult ApplyDamage(IDamageable target, CombatantType attackerType, float requestedDamage)
        {
            if (target == null)
            {
                return DamageResult.Rejected("Target is required.");
            }

            if (!CanDamage(attackerType, target.CombatantType))
            {
                Debug.LogWarning("DamageService rejected " + attackerType + " -> " + target.CombatantType);
                return DamageResult.Rejected("Damage pair is forbidden.");
            }

            float applied = Math.Max(0f, requestedDamage);
            target.ApplyDamage(applied);
            return DamageResult.Applied(applied, target.Health <= 0f);
        }
    }

    public interface IDamageable
    {
        CombatantType CombatantType { get; }
        float Health { get; }
        void ApplyDamage(float amount);
    }

    public readonly struct DamageResult
    {
        private DamageResult(bool allowed, float appliedDamage, bool killed, string error)
        {
            Allowed = allowed;
            AppliedDamage = appliedDamage;
            Killed = killed;
            Error = error ?? string.Empty;
        }

        public bool Allowed { get; }
        public float AppliedDamage { get; }
        public bool Killed { get; }
        public string Error { get; }

        public static DamageResult Applied(float amount, bool killed)
        {
            return new DamageResult(true, amount, killed, string.Empty);
        }

        public static DamageResult Rejected(string error)
        {
            return new DamageResult(false, 0f, false, error);
        }
    }
}

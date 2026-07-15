using System.Collections.Generic;

namespace SurvivalWorld.Server.Combat
{
    public sealed class TargetFilter
    {
        private readonly DamageService damageService;

        public TargetFilter(DamageService damageService)
        {
            this.damageService = damageService ?? new DamageService();
        }

        public List<T> FilterAllowedTargets<T>(CombatantType attackerType, IEnumerable<T> candidates) where T : IDamageable
        {
            var result = new List<T>();
            if (candidates == null)
            {
                return result;
            }

            foreach (T candidate in candidates)
            {
                if (candidate != null && damageService.CanDamage(attackerType, candidate.CombatantType))
                {
                    result.Add(candidate);
                }
            }

            return result;
        }
    }
}

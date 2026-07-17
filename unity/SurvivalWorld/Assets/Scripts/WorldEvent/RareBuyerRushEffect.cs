using System;

namespace SurvivalWorld.Server.WorldEvents
{
    public sealed class RareBuyerRushEffect : WorldEventEffectBase
    {
        public const int DurationSeconds = 10 * 60;
        public const int BuyerCount = 3;

        public RareBuyerRushEffect()
            : base(WorldEventTemplateIds.RareBuyerRush, DurationSeconds * 1000L)
        {
        }

        public override void Tick(WorldEventEffectContext context)
        {
            if (context == null || Stats.Spawned >= BuyerCount)
            {
                return;
            }

            int activeCapacity = Math.Max(0, BuyerCount - Math.Max(Stats.Spawned, context.SpawnSink.ActiveRareBuyers));
            for (int i = 0; i < activeCapacity; i++)
            {
                int seed = StableInventorySeed(context.Config.EventInstanceId, Stats.Spawned);
                if (!context.SpawnSink.SpawnRareBuyer(context.Config.EventInstanceId, context.Config.RegionId, seed))
                {
                    break;
                }

                Stats.AddSpawned(1);
            }

            Stats.SetRemaining(context.SpawnSink.ActiveRareBuyers);
        }

        public override void Complete(WorldEventEffectContext context)
        {
            if (context != null)
            {
                Stats.SetRemaining(context.SpawnSink.ActiveRareBuyers);
            }
        }

        private static int StableInventorySeed(string eventInstanceId, int index)
        {
            unchecked
            {
                int hash = 17;
                string text = eventInstanceId ?? string.Empty;
                for (int i = 0; i < text.Length; i++)
                {
                    hash = hash * 31 + text[i];
                }

                return hash * 31 + index;
            }
        }
    }
}
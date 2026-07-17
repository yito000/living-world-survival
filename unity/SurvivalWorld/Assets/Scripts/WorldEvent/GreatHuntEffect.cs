using System;

namespace SurvivalWorld.Server.WorldEvents
{
    public sealed class GreatHuntEffect : WorldEventEffectBase
    {
        public const int DurationSeconds = 15 * 60;
        public const int AliveCapIncrease = 40;
        public const int TotalSpawnCap = 100;
        private const int SpawnBatchSize = 5;

        public GreatHuntEffect()
            : base(WorldEventTemplateIds.GreatHunt, DurationSeconds * 1000L)
        {
        }

        public override void Tick(WorldEventEffectContext context)
        {
            if (context == null || Stats.Spawned >= TotalSpawnCap)
            {
                return;
            }

            int aliveCapacity = Math.Max(0, AliveCapIncrease - context.SpawnSink.AliveRareDeer);
            int totalCapacity = Math.Max(0, TotalSpawnCap - Stats.Spawned);
            int spawnCount = Math.Min(SpawnBatchSize, Math.Min(aliveCapacity, totalCapacity));
            for (int i = 0; i < spawnCount; i++)
            {
                if (!context.SpawnSink.SpawnRareDeer(context.Config.EventInstanceId, context.Config.RegionId))
                {
                    break;
                }

                Stats.AddSpawned(1);
            }

            Stats.SetRemaining(context.SpawnSink.AliveRareDeer);
        }

        public override void Complete(WorldEventEffectContext context)
        {
            if (context != null)
            {
                Stats.SetRemaining(context.SpawnSink.AliveRareDeer);
            }
        }
    }
}
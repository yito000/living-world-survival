using System;

namespace SurvivalWorld.Server.WorldEvents
{
    public sealed class RareResourceEffect : WorldEventEffectBase
    {
        public const int DurationSeconds = 15 * 60;
        public const int NodeCap = 20;
        public const int DefaultTotalYieldBudget = 200;
        public const int DefaultYieldPerNode = 10;
        private const int SpawnBatchSize = 2;

        private readonly int totalYieldBudget;
        private readonly int yieldPerNode;
        private int allocatedYield;

        public RareResourceEffect(WorldEventParameterBag parameters)
            : base(WorldEventTemplateIds.RareResource, DurationSeconds * 1000L)
        {
            WorldEventParameterBag safeParameters = parameters ?? WorldEventParameterBag.Empty;
            totalYieldBudget = safeParameters.TryGetInt("total_yield_budget", out int budget) && budget > 0
                ? budget
                : DefaultTotalYieldBudget;
            yieldPerNode = safeParameters.TryGetInt("yield_per_node", out int perNode) && perNode > 0
                ? perNode
                : DefaultYieldPerNode;
        }

        public override void Tick(WorldEventEffectContext context)
        {
            if (context == null || Stats.Spawned >= NodeCap || allocatedYield >= totalYieldBudget)
            {
                return;
            }

            int nodeCapacity = Math.Max(0, NodeCap - Math.Max(Stats.Spawned, context.SpawnSink.ActiveRareOreNodes));
            int spawnCount = Math.Min(SpawnBatchSize, nodeCapacity);
            for (int i = 0; i < spawnCount; i++)
            {
                int remainingBudget = totalYieldBudget - allocatedYield;
                if (remainingBudget <= 0)
                {
                    break;
                }

                int nodeYield = Math.Min(yieldPerNode, remainingBudget);
                if (!context.SpawnSink.SpawnRareOreNode(context.Config.EventInstanceId, context.Config.RegionId, nodeYield))
                {
                    break;
                }

                allocatedYield += nodeYield;
                Stats.AddSpawned(1);
            }

            Stats.SetRemaining(context.SpawnSink.ActiveRareOreNodes);
        }

        public override void Complete(WorldEventEffectContext context)
        {
            if (context != null)
            {
                Stats.SetRemaining(context.SpawnSink.ActiveRareOreNodes);
            }
        }
    }
}
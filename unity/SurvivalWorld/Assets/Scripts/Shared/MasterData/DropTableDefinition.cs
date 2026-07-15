using System;
using System.Collections.Generic;
using System.Linq;

namespace SurvivalWorld.Shared.MasterData
{
    public sealed class DropTableDefinition
    {
        public DropTableDefinition(string species, IEnumerable<ItemStack> guaranteedDrops, IEnumerable<ItemStack> rareDrops)
        {
            Species = species ?? string.Empty;
            GuaranteedDrops = guaranteedDrops == null ? Array.Empty<ItemStack>() : guaranteedDrops.ToArray();
            RareDrops = rareDrops == null ? Array.Empty<ItemStack>() : rareDrops.ToArray();
        }

        public string Species { get; }
        public IReadOnlyList<ItemStack> GuaranteedDrops { get; }
        public IReadOnlyList<ItemStack> RareDrops { get; }
    }
}

using System.Collections.Generic;
using System.Linq;

namespace SurvivalWorld.Inventory
{
    public sealed class InventorySnapshot
    {
        public InventorySnapshot(string ownerType, string ownerId, long version, IEnumerable<InventoryEntry> entries)
        {
            OwnerType = ownerType ?? string.Empty;
            OwnerId = ownerId ?? string.Empty;
            Version = version;
            Entries = entries == null ? new List<InventoryEntry>() : entries.ToList();
        }

        public string OwnerType { get; }
        public string OwnerId { get; }
        public long Version { get; }
        public IReadOnlyList<InventoryEntry> Entries { get; }

        public static InventorySnapshot FromOwner(InventoryOwner owner)
        {
            return new InventorySnapshot(owner.OwnerType, owner.OwnerId, owner.Version, owner.Entries);
        }
    }
}

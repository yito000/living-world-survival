using System;
using System.Collections.Generic;

namespace SurvivalWorld.Inventory
{
    public sealed class InventoryOwner
    {
        public const int DefaultSlotCapacity = 24;
        public const float DefaultWeightCapacity = 40.0f;

        public InventoryOwner(string ownerType, string ownerId)
            : this(ownerType, ownerId, DefaultSlotCapacity, DefaultWeightCapacity, 0)
        {
        }

        public InventoryOwner(string ownerType, string ownerId, int slotCapacity, float weightCapacity, long version)
        {
            if (string.IsNullOrWhiteSpace(ownerType))
            {
                throw new ArgumentException("Owner type must not be empty.", nameof(ownerType));
            }

            if (string.IsNullOrWhiteSpace(ownerId))
            {
                throw new ArgumentException("Owner id must not be empty.", nameof(ownerId));
            }

            OwnerType = ownerType;
            OwnerId = ownerId;
            SlotCapacity = slotCapacity;
            WeightCapacity = weightCapacity;
            Version = version;
            Entries = new List<InventoryEntry>();
        }

        public string OwnerType { get; }
        public string OwnerId { get; }
        public int SlotCapacity { get; }
        public float WeightCapacity { get; }
        public long Version { get; internal set; }
        public List<InventoryEntry> Entries { get; }
    }
}

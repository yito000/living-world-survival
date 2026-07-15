using System;
using System.Collections.Generic;
using SurvivalWorld.Inventory;
using SurvivalWorld.Shared;

namespace SurvivalWorld.Server.AI
{
    public sealed class AIPersonalState
    {
        public const float FoodUrgencyThreshold = 60f;
        public const float DefaultWasteNormalizer = 10f;

        private readonly List<AIWantedItem> wantedItems = new List<AIWantedItem>();

        public AIPersonalState(string actorId)
        {
            ActorId = string.IsNullOrWhiteSpace(actorId) ? "ai" : actorId;
            Version = 1L;
            Hunger = SurvivalTuning.HungerInitial;
            WealthGoal = 100L;
            WasteNormalizer = DefaultWasteNormalizer;
            Personality = AIPersonality.FromSeed(ActorId.GetHashCode());
            ActionState = new AIActionState();
            Recalculate();
        }

        public string ActorId { get; }
        public long Version { get; private set; }
        public float Hunger { get; private set; }
        public int UsedSlots { get; private set; }
        public int CapacitySlots { get; private set; } = InventoryOwner.DefaultSlotCapacity;
        public int SellableCount { get; private set; }
        public long NetWorth { get; private set; }
        public long WealthGoal { get; private set; }
        public float NearbyWasteWeight { get; private set; }
        public float WasteNormalizer { get; private set; }
        public AIUrgencySnapshot Urgency { get; private set; }
        public AIPersonality Personality { get; private set; }
        public AIActionState ActionState { get; }
        public IReadOnlyList<AIWantedItem> WantedItems => wantedItems;

        public void UpdateNeeds(AIPersonalStateInput input)
        {
            Hunger = input.Hunger;
            UsedSlots = Math.Max(0, input.UsedSlots);
            CapacitySlots = Math.Max(0, input.CapacitySlots);
            SellableCount = Math.Max(0, input.SellableCount);
            NetWorth = Math.Max(0L, input.NetWorth);
            WealthGoal = Math.Max(0L, input.WealthGoal);
            NearbyWasteWeight = Math.Max(0f, input.NearbyWasteWeight);
            WasteNormalizer = input.WasteNormalizer <= 0f ? DefaultWasteNormalizer : input.WasteNormalizer;
            Recalculate();
            Version++;
        }

        public void UpdateFromInventory(float hunger, AIInventorySummary inventory, float nearbyWasteWeight, long wealthGoal)
        {
            UpdateNeeds(new AIPersonalStateInput(
                hunger,
                inventory.UsedSlots,
                inventory.CapacitySlots,
                inventory.SellableCount,
                inventory.NetWorth,
                wealthGoal,
                nearbyWasteWeight,
                WasteNormalizer));
        }

        public void AddWantedItem(AIWantedItem item)
        {
            wantedItems.Add(item);
            Version++;
        }

        public void RecordFailure()
        {
            ActionState.FailureCount++;
            Version++;
        }

        public void StartTemplate(string templateId, int templateVersion, long startedAtUnixMs, long leaseUntilUnixMs)
        {
            ActionState.ActiveTemplateId = templateId ?? string.Empty;
            ActionState.TemplateVersion = templateVersion;
            ActionState.StartedAtUnixMs = startedAtUnixMs;
            ActionState.LeaseUntilUnixMs = leaseUntilUnixMs;
            ActionState.FailureCount = 0;
            Version++;
        }

        public void ClearTemplate(long nowUnixMs)
        {
            ActionState.ActiveTemplateId = string.Empty;
            ActionState.TemplateVersion = 0;
            ActionState.StartedAtUnixMs = 0L;
            ActionState.LeaseUntilUnixMs = nowUnixMs;
            ActionState.TargetRefs.Clear();
            Version++;
        }

        private void Recalculate()
        {
            Urgency = CalculateUrgency(Hunger, UsedSlots, CapacitySlots, NetWorth, WealthGoal, NearbyWasteWeight, WasteNormalizer);
        }

        public static AIUrgencySnapshot CalculateUrgency(float hunger, int usedSlots, int capacitySlots, long netWorth, long wealthGoal, float nearbyWasteWeight, float wasteNormalizer)
        {
            float food = UrgencyFood(hunger);
            float cleanup = UrgencyCleanup(usedSlots, capacitySlots);
            float earn = UrgencyEarn(wealthGoal, netWorth);
            float wealth = WealthScore(wealthGoal, netWorth);
            float inventoryPressure = capacitySlots <= 0 ? (usedSlots > 0 ? 1f : 0f) : usedSlots / (float)capacitySlots;
            float cleanlinessPressure = wasteNormalizer <= 0f ? 0f : nearbyWasteWeight / wasteNormalizer;
            float needScore = Math.Max(food, Math.Max(cleanup, earn));
            return new AIUrgencySnapshot(food, cleanup, earn, needScore, inventoryPressure, cleanlinessPressure, wealth);
        }

        public static float NeedScore(float threshold, float currentValue)
        {
            if (threshold <= 0f)
            {
                return currentValue <= 0f ? 1f : 0f;
            }

            return Clamp01((threshold - currentValue) / threshold);
        }

        public static float UrgencyFood(float hunger)
        {
            return Clamp01((FoodUrgencyThreshold - hunger) / FoodUrgencyThreshold);
        }

        public static float UrgencyCleanup(int usedSlots, int capacitySlots)
        {
            if (capacitySlots <= 0)
            {
                return usedSlots > 0 ? 1f : 0f;
            }

            return Clamp01((usedSlots - capacitySlots) / (float)capacitySlots);
        }

        public static float UrgencyEarn(long wealthGoal, long netWorth)
        {
            long denominator = Math.Max(wealthGoal, 1L);
            return Clamp01((wealthGoal - netWorth) / (float)denominator);
        }

        public static float WealthScore(long wealthGoal, long netWorth)
        {
            return UrgencyEarn(wealthGoal, netWorth);
        }

        public static float Clamp01(float value)
        {
            if (value <= 0f)
            {
                return 0f;
            }

            return value >= 1f ? 1f : value;
        }
    }

    public readonly struct AIPersonalStateInput
    {
        public AIPersonalStateInput(float hunger, int usedSlots, int capacitySlots, int sellableCount, long netWorth, long wealthGoal, float nearbyWasteWeight, float wasteNormalizer)
        {
            Hunger = hunger;
            UsedSlots = usedSlots;
            CapacitySlots = capacitySlots;
            SellableCount = sellableCount;
            NetWorth = netWorth;
            WealthGoal = wealthGoal;
            NearbyWasteWeight = nearbyWasteWeight;
            WasteNormalizer = wasteNormalizer;
        }

        public float Hunger { get; }
        public int UsedSlots { get; }
        public int CapacitySlots { get; }
        public int SellableCount { get; }
        public long NetWorth { get; }
        public long WealthGoal { get; }
        public float NearbyWasteWeight { get; }
        public float WasteNormalizer { get; }
    }

    public readonly struct AIUrgencySnapshot
    {
        public AIUrgencySnapshot(float food, float cleanup, float earn, float needScore, float inventoryPressure, float cleanlinessPressure, float wealthScore)
        {
            Food = food;
            Cleanup = cleanup;
            Earn = earn;
            NeedScore = needScore;
            InventoryPressure = inventoryPressure;
            CleanlinessPressure = cleanlinessPressure;
            WealthScore = wealthScore;
        }

        public float Food { get; }
        public float Cleanup { get; }
        public float Earn { get; }
        public float NeedScore { get; }
        public float InventoryPressure { get; }
        public float CleanlinessPressure { get; }
        public float WealthScore { get; }
    }

    public sealed class AIActionState
    {
        public string ActiveTemplateId { get; set; } = string.Empty;
        public int TemplateVersion { get; set; }
        public long StartedAtUnixMs { get; set; }
        public long LeaseUntilUnixMs { get; set; }
        public Dictionary<string, string> TargetRefs { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public int FailureCount { get; set; }
    }

    public readonly struct AIPersonality
    {
        public AIPersonality(float greed, float tidiness, float patience, float curiosity, float eventPreference)
        {
            Greed = AIPersonalState.Clamp01(greed);
            Tidiness = AIPersonalState.Clamp01(tidiness);
            Patience = AIPersonalState.Clamp01(patience);
            Curiosity = AIPersonalState.Clamp01(curiosity);
            EventPreference = AIPersonalState.Clamp01(eventPreference);
        }

        public float Greed { get; }
        public float Tidiness { get; }
        public float Patience { get; }
        public float Curiosity { get; }
        public float EventPreference { get; }

        public static AIPersonality FromSeed(int seed)
        {
            var random = new Random(seed);
            return new AIPersonality(
                (float)random.NextDouble(),
                (float)random.NextDouble(),
                (float)random.NextDouble(),
                (float)random.NextDouble(),
                (float)random.NextDouble());
        }
    }

    public readonly struct AIWantedItem
    {
        public AIWantedItem(string itemTag, int priority, long maxBudget, string reason, long expiresAtUnixMs, string[] substituteTags)
        {
            ItemTag = itemTag ?? string.Empty;
            Priority = priority;
            MaxBudget = maxBudget;
            Reason = reason ?? string.Empty;
            ExpiresAtUnixMs = expiresAtUnixMs;
            SubstituteTags = substituteTags ?? Array.Empty<string>();
        }

        public string ItemTag { get; }
        public int Priority { get; }
        public long MaxBudget { get; }
        public string Reason { get; }
        public long ExpiresAtUnixMs { get; }
        public string[] SubstituteTags { get; }
    }
}

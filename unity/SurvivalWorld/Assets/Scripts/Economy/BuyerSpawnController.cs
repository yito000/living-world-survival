using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SurvivalWorld.Economy
{
    public sealed class BuyerSpawnPlan
    {
        public BuyerSpawnPlan(string idempotencyKey, long seed, long spawnAtUnixMs, long despawnAtUnixMs)
        {
            IdempotencyKey = idempotencyKey ?? string.Empty;
            Seed = seed;
            SpawnAtUnixMs = spawnAtUnixMs;
            DespawnAtUnixMs = despawnAtUnixMs;
        }

        public string IdempotencyKey { get; }
        public long Seed { get; }
        public long SpawnAtUnixMs { get; }
        public long DespawnAtUnixMs { get; }
    }

    public sealed class BuyerSpawnController
    {
        public const long BaseSpawnIntervalMs = 30L * 60L * 1000L;
        public const long SpawnJitterMs = 10L * 60L * 1000L;
        public const long StayDurationMs = 10L * 60L * 1000L;
        public const long PreparingLeadMs = 30L * 1000L;

        private readonly IEconomyClient economyClient;
        private readonly BuyerRegistry buyerRegistry;
        private readonly string worldId;
        private readonly string regionId;
        private readonly string inventoryTableId;
        private readonly int priceModifierBasisPoints;

        public BuyerSpawnController(
            IEconomyClient economyClient,
            BuyerRegistry buyerRegistry,
            string worldId,
            string regionId,
            string inventoryTableId,
            int priceModifierBasisPoints)
        {
            this.economyClient = economyClient ?? NullEconomyClient.Instance;
            this.buyerRegistry = buyerRegistry ?? throw new ArgumentNullException(nameof(buyerRegistry));
            this.worldId = worldId ?? string.Empty;
            this.regionId = regionId ?? string.Empty;
            this.inventoryTableId = inventoryTableId ?? string.Empty;
            this.priceModifierBasisPoints = priceModifierBasisPoints <= 0 ? 10000 : priceModifierBasisPoints;
        }

        public BuyerSpawnPlan CreatePlan(long nowUnixMs, int ordinal)
        {
            long seed = StableSeed(worldId, regionId, ordinal);
            long jitter = PositiveModulo(seed, SpawnJitterMs * 2L + 1L) - SpawnJitterMs;
            long spawnAt = nowUnixMs + BaseSpawnIntervalMs + jitter;
            long despawnAt = spawnAt + StayDurationMs;
            string idempotencyKey = string.Format(CultureInfo.InvariantCulture, "buyer:{0}:{1}:{2}", worldId, regionId, ordinal);
            return new BuyerSpawnPlan(idempotencyKey, seed, spawnAt, despawnAt);
        }

        public async Task<BuyerInstance> SpawnAsync(BuyerSpawnPlan plan, Vector3 markerPosition, CancellationToken cancellationToken)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            var request = new RegisterBuyerRequestData
            {
                IdempotencyKey = plan.IdempotencyKey,
                WorldId = worldId,
                RegionId = regionId,
                Seed = plan.Seed,
                InventoryTableId = inventoryTableId,
                PriceModifierBasisPoints = priceModifierBasisPoints,
                SpawnAtUnixMs = plan.SpawnAtUnixMs,
                DespawnAtUnixMs = plan.DespawnAtUnixMs
            };

            RegisterBuyerResult result = await economyClient.RegisterBuyerAsync(request, cancellationToken);
            if (!result.Success)
            {
                return null;
            }

            var buyer = new BuyerInstance(result.BuyerInstanceId, regionId, markerPosition, plan.SpawnAtUnixMs, plan.DespawnAtUnixMs, result.Stock);
            buyer.MarkActive();
            buyerRegistry.AddOrReplace(buyer);
            return buyer;
        }

        public async Task<bool> BeginPreparingAsync(string buyerInstanceId, CancellationToken cancellationToken)
        {
            if (!buyerRegistry.TryGet(buyerInstanceId, out BuyerInstance buyer))
            {
                return false;
            }

            buyer.BeginPreparing();
            BuyerDespawnResult result = await economyClient.DespawnBuyerAsync(buyerInstanceId, BuyerStatus.Preparing, cancellationToken);
            return result.Success;
        }

        public async Task<bool> TryDespawnAsync(string buyerInstanceId, CancellationToken cancellationToken)
        {
            if (!buyerRegistry.TryGet(buyerInstanceId, out BuyerInstance buyer) || !buyer.CanDespawn)
            {
                return false;
            }

            BuyerDespawnResult result = await economyClient.DespawnBuyerAsync(buyerInstanceId, BuyerStatus.Despawned, cancellationToken);
            if (!result.Success)
            {
                return false;
            }

            buyer.MarkDespawned();
            buyerRegistry.Remove(buyerInstanceId);
            return true;
        }

        public BuyerInstance SpawnLocalRareBuyer(string eventInstanceId, string region, int inventorySeed, Vector3 markerPosition, IReadOnlyList<BuyerStockEntry> stock)
        {
            string buyerId = string.Format(CultureInfo.InvariantCulture, "rare-buyer:{0}:{1}", eventInstanceId ?? string.Empty, inventorySeed);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var buyer = new BuyerInstance(buyerId, string.IsNullOrWhiteSpace(region) ? regionId : region, markerPosition, now, now + StayDurationMs, stock ?? Array.Empty<BuyerStockEntry>());
            buyer.MarkActive();
            buyerRegistry.AddOrReplace(buyer);
            return buyer;
        }

        private static long StableSeed(string worldId, string regionId, int ordinal)
        {
            unchecked
            {
                long hash = 1469598103934665603L;
                string text = (worldId ?? string.Empty) + ":" + (regionId ?? string.Empty) + ":" + ordinal.ToString(CultureInfo.InvariantCulture);
                for (int i = 0; i < text.Length; i++)
                {
                    hash ^= text[i];
                    hash *= 1099511628211L;
                }

                return hash;
            }
        }

        private static long PositiveModulo(long value, long divisor)
        {
            long result = value % divisor;
            return result < 0 ? result + divisor : result;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace SurvivalWorld.Economy
{
    public enum BuyerStatus
    {
        Spawning = 0,
        Active = 1,
        Preparing = 2,
        Despawned = 3
    }

    public sealed class BuyerStockEntry
    {
        public BuyerStockEntry(string stockEntryId, string itemDefinitionId, long unitPriceAmount, int remainingQuantity)
        {
            StockEntryId = stockEntryId ?? string.Empty;
            ItemDefinitionId = itemDefinitionId ?? string.Empty;
            UnitPriceAmount = unitPriceAmount;
            RemainingQuantity = Math.Max(0, remainingQuantity);
        }

        public string StockEntryId { get; }
        public string ItemDefinitionId { get; }
        public long UnitPriceAmount { get; }
        public int RemainingQuantity { get; private set; }

        public bool HasStock => RemainingQuantity > 0;

        public void ApplyCommittedQuantity(int quantity)
        {
            RemainingQuantity = Math.Max(0, RemainingQuantity - Math.Max(0, quantity));
        }
    }

    public sealed class BuyerInstance
    {
        private readonly Dictionary<string, BuyerStockEntry> stockById = new Dictionary<string, BuyerStockEntry>(StringComparer.Ordinal);
        private int pendingTransactions;

        public BuyerInstance(
            string buyerInstanceId,
            string regionId,
            Vector3 position,
            long spawnAtUnixMs,
            long despawnAtUnixMs,
            IEnumerable<BuyerStockEntry> stock)
        {
            BuyerInstanceId = buyerInstanceId ?? string.Empty;
            RegionId = regionId ?? string.Empty;
            Position = position;
            SpawnAtUnixMs = spawnAtUnixMs;
            DespawnAtUnixMs = despawnAtUnixMs;
            Status = BuyerStatus.Spawning;

            if (stock != null)
            {
                foreach (BuyerStockEntry entry in stock)
                {
                    if (entry != null && !string.IsNullOrWhiteSpace(entry.StockEntryId))
                    {
                        stockById[entry.StockEntryId] = entry;
                    }
                }
            }
        }

        public string BuyerInstanceId { get; }
        public string RegionId { get; }
        public Vector3 Position { get; private set; }
        public long SpawnAtUnixMs { get; }
        public long DespawnAtUnixMs { get; }
        public BuyerStatus Status { get; private set; }
        public int PendingTransactions => pendingTransactions;
        public IReadOnlyCollection<BuyerStockEntry> Stock => stockById.Values;
        public bool CanAcceptNewPurchase => Status == BuyerStatus.Active;
        public bool CanDespawn => Status == BuyerStatus.Preparing && pendingTransactions == 0;

        public void SetPosition(Vector3 position)
        {
            Position = position;
        }

        public void MarkActive()
        {
            if (Status == BuyerStatus.Despawned)
            {
                return;
            }

            Status = BuyerStatus.Active;
        }

        public void BeginPreparing()
        {
            if (Status == BuyerStatus.Active || Status == BuyerStatus.Spawning)
            {
                Status = BuyerStatus.Preparing;
            }
        }

        public void MarkDespawned()
        {
            Status = BuyerStatus.Despawned;
        }

        public bool TryGetStock(string stockEntryId, out BuyerStockEntry stock)
        {
            return stockById.TryGetValue(stockEntryId ?? string.Empty, out stock);
        }

        public bool TryBeginTransaction()
        {
            if (!CanAcceptNewPurchase)
            {
                return false;
            }

            pendingTransactions++;
            return true;
        }

        public void CompleteTransaction()
        {
            pendingTransactions = Math.Max(0, pendingTransactions - 1);
        }

        public void ApplyCommittedPurchase(string stockEntryId, int quantity)
        {
            if (TryGetStock(stockEntryId, out BuyerStockEntry stock))
            {
                stock.ApplyCommittedQuantity(quantity);
            }
        }

        public static string StableIdempotencyKey(string worldId, string buyerInstanceId, string purchaserId, string commandId)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "purchase:{0}:{1}:{2}:{3}",
                worldId ?? string.Empty,
                buyerInstanceId ?? string.Empty,
                purchaserId ?? string.Empty,
                commandId ?? string.Empty);
        }
    }

    public sealed class BuyerRegistry
    {
        private readonly Dictionary<string, BuyerInstance> buyers = new Dictionary<string, BuyerInstance>(StringComparer.Ordinal);

        public IReadOnlyCollection<BuyerInstance> Buyers => buyers.Values;

        public void AddOrReplace(BuyerInstance buyer)
        {
            if (buyer == null || string.IsNullOrWhiteSpace(buyer.BuyerInstanceId))
            {
                return;
            }

            buyers[buyer.BuyerInstanceId] = buyer;
        }

        public bool TryGet(string buyerInstanceId, out BuyerInstance buyer)
        {
            return buyers.TryGetValue(buyerInstanceId ?? string.Empty, out buyer);
        }

        public bool Remove(string buyerInstanceId)
        {
            return buyers.Remove(buyerInstanceId ?? string.Empty);
        }
    }
}

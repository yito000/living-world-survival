using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Survival.V1;
using SurvivalWorld.Inventory;

namespace SurvivalWorld.Economy
{
    public enum BuyerPurchaseResultStatus
    {
        Committed = 0,
        Duplicate = 1,
        OutOfStock = 2,
        InsufficientFunds = 3,
        Rejected = 4,
        Conflict = 5,
        Pending = 6
    }

    public sealed class BuyerPurchaseContext
    {
        public string WorldId { get; set; }
        public string OwnerType { get; set; }
        public string PurchaserId { get; set; }
        public string CommandId { get; set; }
        public string BuyerInstanceId { get; set; }
        public string StockEntryId { get; set; }
        public long InventoryVersion { get; set; }
    }

    public sealed class BuyerPurchaseResult
    {
        private BuyerPurchaseResult(
            BuyerPurchaseResultStatus status,
            string error,
            InventorySnapshot snapshot,
            PurchaseStatus apiStatus,
            bool runtimeReflected,
            bool resyncRequested,
            bool snapshotApplied,
            long chargedAmount)
        {
            Status = status;
            Error = error ?? string.Empty;
            Snapshot = snapshot;
            ApiStatus = apiStatus;
            RuntimeReflected = runtimeReflected;
            ResyncRequested = resyncRequested;
            SnapshotApplied = snapshotApplied;
            ChargedAmount = chargedAmount;
        }

        public BuyerPurchaseResultStatus Status { get; }
        public string Error { get; }
        public InventorySnapshot Snapshot { get; }
        public PurchaseStatus ApiStatus { get; }
        public bool RuntimeReflected { get; }
        public bool ResyncRequested { get; }
        public bool SnapshotApplied { get; }
        public long ChargedAmount { get; }
        public bool Success => Status == BuyerPurchaseResultStatus.Committed || Status == BuyerPurchaseResultStatus.Duplicate;

        public static BuyerPurchaseResult Rejected(string error, InventorySnapshot snapshot = null)
        {
            return new BuyerPurchaseResult(BuyerPurchaseResultStatus.Rejected, error, snapshot, PurchaseStatus.Rejected, false, false, false, 0L);
        }

        public static BuyerPurchaseResult Conflict(string error, InventorySnapshot snapshot)
        {
            return new BuyerPurchaseResult(BuyerPurchaseResultStatus.Conflict, error, snapshot, PurchaseStatus.Rejected, false, false, false, 0L);
        }

        public static BuyerPurchaseResult FromApi(PurchaseStatus apiStatus, InventoryMutationResult mutation, InventoryReconcileResult reconcile, bool reflected, long chargedAmount)
        {
            BuyerPurchaseResultStatus mapped = Map(apiStatus, mutation);
            InventorySnapshot snapshot = reconcile == null ? mutation?.Snapshot : reconcile.Snapshot;
            return new BuyerPurchaseResult(
                mapped,
                mutation == null ? string.Empty : mutation.Error,
                snapshot,
                apiStatus,
                reflected,
                reconcile != null && reconcile.RequestedSnapshot,
                reconcile != null && reconcile.AppliedSnapshot,
                chargedAmount);
        }

        public static BuyerPurchaseResult FromApiFailure(PurchaseStatus apiStatus)
        {
            return new BuyerPurchaseResult(Map(apiStatus, null), string.Empty, null, apiStatus, false, false, false, 0L);
        }

        public static BuyerPurchaseResult Pending(string error)
        {
            return new BuyerPurchaseResult(BuyerPurchaseResultStatus.Pending, error, null, PurchaseStatus.Unspecified, false, false, false, 0L);
        }

        private static BuyerPurchaseResultStatus Map(PurchaseStatus apiStatus, InventoryMutationResult mutation)
        {
            if (mutation != null && mutation.Status == InventoryMutationStatus.Conflict)
            {
                return BuyerPurchaseResultStatus.Conflict;
            }

            switch (apiStatus)
            {
                case PurchaseStatus.Committed:
                    return BuyerPurchaseResultStatus.Committed;
                case PurchaseStatus.Duplicate:
                    return BuyerPurchaseResultStatus.Duplicate;
                case PurchaseStatus.OutOfStock:
                    return BuyerPurchaseResultStatus.OutOfStock;
                case PurchaseStatus.InsufficientFunds:
                    return BuyerPurchaseResultStatus.InsufficientFunds;
                default:
                    return BuyerPurchaseResultStatus.Rejected;
            }
        }
    }

    public sealed class PurchaseProtocol
    {
        private readonly IEconomyClient economyClient;
        private readonly InventoryRuntimeService inventoryRuntime;
        private readonly InventoryReconciler reconciler;
        private readonly int maxAttempts;

        public PurchaseProtocol(IEconomyClient economyClient, InventoryRuntimeService inventoryRuntime, InventoryReconciler reconciler, int maxAttempts = 2)
        {
            this.economyClient = economyClient ?? NullEconomyClient.Instance;
            this.inventoryRuntime = inventoryRuntime ?? throw new ArgumentNullException(nameof(inventoryRuntime));
            this.reconciler = reconciler ?? new InventoryReconciler(inventoryRuntime, NullInventorySnapshotProvider.Instance);
            this.maxAttempts = Math.Max(1, maxAttempts);
        }

        public async Task<BuyerPurchaseResult> CommitPurchaseAsync(BuyerPurchaseContext context, CancellationToken cancellationToken)
        {
            if (context == null)
            {
                return BuyerPurchaseResult.Rejected("Purchase context is required.");
            }

            string idempotencyKey = BuyerInstance.StableIdempotencyKey(context.WorldId, context.BuyerInstanceId, context.PurchaserId, context.CommandId);
            var request = new CommitPurchaseRequest
            {
                IdempotencyKey = idempotencyKey,
                BuyerInstanceId = context.BuyerInstanceId ?? string.Empty,
                StockEntryId = context.StockEntryId ?? string.Empty,
                PurchaserId = context.PurchaserId ?? string.Empty,
                InventoryVersion = context.InventoryVersion
            };

            CommitPurchaseResponse response = null;
            Exception lastException = null;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                try
                {
                    response = await economyClient.CommitPurchaseAsync(request, cancellationToken);
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                }
            }

            if (response == null)
            {
                return BuyerPurchaseResult.Pending(lastException == null ? "Purchase result is pending." : lastException.Message);
            }

            if (response.Status != PurchaseStatus.Committed && response.Status != PurchaseStatus.Duplicate)
            {
                return BuyerPurchaseResult.FromApiFailure(response.Status);
            }

            InventoryMutationResult mutation = inventoryRuntime.ApplyApiGrantedItems(
                context.OwnerType,
                context.PurchaserId,
                context.CommandId,
                context.InventoryVersion,
                response.GrantedItems,
                response.ItemInstanceIds);

            bool reflected = mutation != null && mutation.Status == InventoryMutationStatus.Ok;
            InventoryReconcileResult reconcile = mutation == null
                ? null
                : reconciler.Reconcile(context.OwnerType, context.PurchaserId, mutation.Snapshot, response.NewPersistedInventoryVersion);

            long chargedAmount = response.Charged == null ? 0L : response.Charged.Amount;
            return BuyerPurchaseResult.FromApi(response.Status, mutation, reconcile, reflected, chargedAmount);
        }
    }
}

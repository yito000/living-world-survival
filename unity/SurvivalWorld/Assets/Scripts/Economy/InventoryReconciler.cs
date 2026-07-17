using System;
using Survival.V1;
using SurvivalWorld.Inventory;

namespace SurvivalWorld.Economy
{
    public interface IInventorySnapshotProvider
    {
        InventorySnapshot RequestFullSnapshot(string ownerType, string ownerId, RequestInventorySnapshot request);
    }

    public sealed class NullInventorySnapshotProvider : IInventorySnapshotProvider
    {
        public static readonly NullInventorySnapshotProvider Instance = new NullInventorySnapshotProvider();

        private NullInventorySnapshotProvider()
        {
        }

        public InventorySnapshot RequestFullSnapshot(string ownerType, string ownerId, RequestInventorySnapshot request)
        {
            return null;
        }
    }

    public sealed class InventoryReconcileResult
    {
        private InventoryReconcileResult(bool matched, bool requestedSnapshot, bool appliedSnapshot, InventorySnapshot snapshot)
        {
            Matched = matched;
            RequestedSnapshot = requestedSnapshot;
            AppliedSnapshot = appliedSnapshot;
            Snapshot = snapshot;
        }

        public bool Matched { get; }
        public bool RequestedSnapshot { get; }
        public bool AppliedSnapshot { get; }
        public InventorySnapshot Snapshot { get; }

        public static InventoryReconcileResult NoMismatch(InventorySnapshot snapshot)
        {
            return new InventoryReconcileResult(true, false, false, snapshot);
        }

        public static InventoryReconcileResult SnapshotRequested(bool appliedSnapshot, InventorySnapshot snapshot)
        {
            return new InventoryReconcileResult(false, true, appliedSnapshot, snapshot);
        }
    }

    public sealed class InventoryReconciler
    {
        private readonly InventoryRuntimeService runtime;
        private readonly IInventorySnapshotProvider snapshotProvider;

        public InventoryReconciler(InventoryRuntimeService runtime, IInventorySnapshotProvider snapshotProvider)
        {
            this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            this.snapshotProvider = snapshotProvider ?? NullInventorySnapshotProvider.Instance;
        }

        public InventoryReconcileResult Reconcile(string ownerType, string ownerId, InventorySnapshot runtimeSnapshot, long persistedVersion)
        {
            if (runtimeSnapshot != null && runtimeSnapshot.Version == persistedVersion)
            {
                return InventoryReconcileResult.NoMismatch(runtimeSnapshot);
            }

            long lastKnownVersion = runtimeSnapshot == null ? -1L : runtimeSnapshot.Version;
            InventorySnapshot fullSnapshot = snapshotProvider.RequestFullSnapshot(
                ownerType,
                ownerId,
                new RequestInventorySnapshot { LastKnownVersion = lastKnownVersion });

            if (fullSnapshot == null)
            {
                return InventoryReconcileResult.SnapshotRequested(false, runtimeSnapshot);
            }

            runtime.ApplySnapshot(ownerType, ownerId, fullSnapshot.Version, fullSnapshot.Entries);
            return InventoryReconcileResult.SnapshotRequested(true, fullSnapshot);
        }
    }
}

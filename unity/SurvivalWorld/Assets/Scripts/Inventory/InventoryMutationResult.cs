using Survival.V1;

namespace SurvivalWorld.Inventory
{
    public enum InventoryMutationStatus
    {
        Ok = 0,
        Duplicate = 1,
        Conflict = 2,
        Rejected = 3
    }

    public sealed class InventoryMutationResult
    {
        private InventoryMutationResult(InventoryMutationStatus status, string error, InventorySnapshot snapshot, DomainEvent domainEvent)
        {
            Status = status;
            Error = error ?? string.Empty;
            Snapshot = snapshot;
            DomainEvent = domainEvent;
        }

        public InventoryMutationStatus Status { get; }
        public string Error { get; }
        public InventorySnapshot Snapshot { get; }
        public DomainEvent DomainEvent { get; }
        public bool Success => Status == InventoryMutationStatus.Ok || Status == InventoryMutationStatus.Duplicate;

        public static InventoryMutationResult Ok(InventorySnapshot snapshot, DomainEvent domainEvent)
        {
            return new InventoryMutationResult(InventoryMutationStatus.Ok, string.Empty, snapshot, domainEvent);
        }

        public static InventoryMutationResult Duplicate(InventorySnapshot snapshot)
        {
            return new InventoryMutationResult(InventoryMutationStatus.Duplicate, string.Empty, snapshot, null);
        }

        public static InventoryMutationResult Conflict(InventorySnapshot snapshot, string error)
        {
            return new InventoryMutationResult(InventoryMutationStatus.Conflict, error, snapshot, null);
        }

        public static InventoryMutationResult Rejected(InventorySnapshot snapshot, string error)
        {
            return new InventoryMutationResult(InventoryMutationStatus.Rejected, error, snapshot, null);
        }

        public ResultStatus ToProtoStatus()
        {
            switch (Status)
            {
                case InventoryMutationStatus.Ok:
                    return ResultStatus.Ok;
                case InventoryMutationStatus.Duplicate:
                    return ResultStatus.Duplicate;
                case InventoryMutationStatus.Conflict:
                    return ResultStatus.Conflict;
                default:
                    return ResultStatus.Rejected;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Survival.V1;
using SurvivalWorld.Inventory;
using SurvivalWorld.World;

namespace SurvivalWorld.Server.Persistence
{
    public sealed class OutboxAgent : IInventoryEventSink
    {
        private readonly IWorldDataGateway gateway;
        private readonly string serverId;
        private readonly Queue<DomainEvent> pending = new Queue<DomainEvent>();

        public OutboxAgent(IWorldDataGateway gateway, string serverId)
        {
            this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
            this.serverId = serverId ?? string.Empty;
        }

        public int PendingCount => pending.Count;
        public string LastConflictAggregateId { get; private set; } = string.Empty;

        public void Enqueue(DomainEvent domainEvent)
        {
            if (domainEvent != null)
            {
                pending.Enqueue(domainEvent);
            }
        }

        public async UniTask<OutboxFlushResult> FlushAsync(CancellationToken cancellationToken)
        {
            LastConflictAggregateId = string.Empty;
            if (pending.Count == 0)
            {
                return OutboxFlushResult.NoPendingEvents();
            }

            DomainEvent[] events = pending.ToArray();
            AppendEventsResponse response = await gateway.AppendEventsAsync(serverId, events, cancellationToken);
            if (response == null || response.Results.Count != events.Length)
            {
                return OutboxFlushResult.Failed("AppendEvents result count mismatch.");
            }

            int accepted = 0;
            for (int i = 0; i < response.Results.Count; i++)
            {
                ResultStatus status = response.Results[i];
                if (status == ResultStatus.Ok || status == ResultStatus.Duplicate)
                {
                    accepted++;
                    continue;
                }

                if (status == ResultStatus.Conflict)
                {
                    LastConflictAggregateId = events[i].AggregateId;
                    return OutboxFlushResult.Conflicted(events[i].AggregateId);
                }

                return OutboxFlushResult.Failed("AppendEvents rejected event " + events[i].EventId + " with " + status);
            }

            pending.Clear();
            return OutboxFlushResult.Accepted(accepted);
        }
    }

    public readonly struct OutboxFlushResult
    {
        private OutboxFlushResult(bool success, bool empty, bool conflict, int acceptedCount, string aggregateId, string error)
        {
            Success = success;
            Empty = empty;
            Conflict = conflict;
            AcceptedCount = acceptedCount;
            AggregateId = aggregateId ?? string.Empty;
            Error = error ?? string.Empty;
        }

        public bool Success { get; }
        public bool Empty { get; }
        public bool Conflict { get; }
        public int AcceptedCount { get; }
        public string AggregateId { get; }
        public string Error { get; }

        public static OutboxFlushResult NoPendingEvents()
        {
            return new OutboxFlushResult(true, true, false, 0, string.Empty, string.Empty);
        }

        public static OutboxFlushResult Accepted(int acceptedCount)
        {
            return new OutboxFlushResult(true, false, false, acceptedCount, string.Empty, string.Empty);
        }

        public static OutboxFlushResult Conflicted(string aggregateId)
        {
            return new OutboxFlushResult(false, false, true, 0, aggregateId, "AppendEvents conflict for aggregate " + aggregateId);
        }

        public static OutboxFlushResult Failed(string error)
        {
            return new OutboxFlushResult(false, false, false, 0, string.Empty, error);
        }
    }
}

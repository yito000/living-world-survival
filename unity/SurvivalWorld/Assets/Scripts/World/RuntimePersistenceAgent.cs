using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Survival.V1;
using SurvivalWorld.Inventory;

namespace SurvivalWorld.World
{
    public sealed class RuntimePersistenceAgent : IInventoryEventSink
    {
        private readonly IWorldDataGateway gateway;
        private readonly WorldRuntimeState runtimeState;
        private readonly string serverId;
        private readonly string worldId;
        private readonly Queue<DomainEvent> outbox = new Queue<DomainEvent>();

        public RuntimePersistenceAgent(IWorldDataGateway gateway, WorldRuntimeState runtimeState, string serverId, string worldId)
        {
            this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
            this.runtimeState = runtimeState ?? throw new ArgumentNullException(nameof(runtimeState));
            this.serverId = serverId ?? string.Empty;
            this.worldId = worldId ?? string.Empty;
        }

        public int PendingEventCount => outbox.Count;

        public void Enqueue(DomainEvent domainEvent)
        {
            if (domainEvent != null)
            {
                outbox.Enqueue(domainEvent);
            }
        }

        public async UniTask<bool> FlushAsync(CancellationToken cancellationToken)
        {
            if (outbox.Count == 0)
            {
                return true;
            }

            DomainEvent[] events = outbox.ToArray();
            AppendEventsResponse response = await gateway.AppendEventsAsync(serverId, events, cancellationToken);
            if (response.Results.Count != events.Length)
            {
                return false;
            }

            for (int i = 0; i < response.Results.Count; i++)
            {
                ResultStatus status = response.Results[i];
                if (status != ResultStatus.Ok && status != ResultStatus.Duplicate)
                {
                    return false;
                }
            }

            outbox.Clear();
            return true;
        }

        public async UniTask<SaveSnapshotResponse> SaveSnapshotAsync(CancellationToken cancellationToken)
        {
            byte[] payload = runtimeState.CreateSnapshotPayload();
            string checksum = SnapshotChecksum.ComputeSha256Hex(payload);
            return await gateway.SaveSnapshotAsync(worldId, runtimeState.CurrentSequence, checksum, payload, cancellationToken);
        }

        public async UniTask RunAsync(TimeSpan outboxFlushInterval, TimeSpan snapshotInterval, CancellationToken cancellationToken)
        {
            TimeSpan safeFlushInterval = outboxFlushInterval <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : outboxFlushInterval;
            TimeSpan safeSnapshotInterval = snapshotInterval <= TimeSpan.Zero ? TimeSpan.FromSeconds(30) : snapshotInterval;
            DateTime nextSnapshotUtc = DateTime.UtcNow + safeSnapshotInterval;

            while (!cancellationToken.IsCancellationRequested)
            {
                await UniTask.Delay(safeFlushInterval, cancellationToken: cancellationToken);
                await FlushAsync(cancellationToken);

                if (DateTime.UtcNow >= nextSnapshotUtc)
                {
                    await SaveSnapshotAsync(cancellationToken);
                    nextSnapshotUtc = DateTime.UtcNow + safeSnapshotInterval;
                }
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Google.Protobuf;
using Survival.V1;

namespace SurvivalWorld.World
{
    public sealed class WorldRuntimeState
    {
        private readonly HashSet<string> appliedEventIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<DomainEvent> appliedEvents = new List<DomainEvent>();

        public long CurrentSequence { get; private set; }
        public ByteString SnapshotPayload { get; private set; } = ByteString.Empty;
        public IReadOnlyList<DomainEvent> AppliedEvents => appliedEvents;

        public void RestoreSnapshot(ByteString snapshotPayload, long sequence)
        {
            SnapshotPayload = snapshotPayload ?? ByteString.Empty;
            CurrentSequence = sequence;
            appliedEventIds.Clear();
            appliedEvents.Clear();
        }

        public int ApplyEventTail(IEnumerable<DomainEvent> eventTail)
        {
            if (eventTail == null)
            {
                return 0;
            }

            int applied = 0;
            foreach (DomainEvent domainEvent in eventTail.OrderBy(e => e.LocalSequence).ThenBy(e => e.EventId, StringComparer.Ordinal))
            {
                if (domainEvent == null || string.IsNullOrWhiteSpace(domainEvent.EventId) || !appliedEventIds.Add(domainEvent.EventId))
                {
                    continue;
                }

                appliedEvents.Add(domainEvent);
                applied++;
            }

            return applied;
        }

        public byte[] CreateSnapshotPayload()
        {
            if (SnapshotPayload.Length > 0)
            {
                return SnapshotPayload.ToByteArray();
            }

            return ByteString.CopyFromUtf8("{\"version\":1}").ToByteArray();
        }
    }
}

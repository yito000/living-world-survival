using System;
using System.Collections.Generic;
using Google.Protobuf;
using Survival.V1;
using SurvivalWorld.World;

namespace SurvivalWorld.Shared.Events
{
    public sealed class DomainEventFactory
    {
        private readonly Dictionary<string, long> localSequenceByAggregate = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly string worldId;

        public DomainEventFactory(string worldId)
        {
            this.worldId = string.IsNullOrWhiteSpace(worldId) ? "runtime" : worldId;
        }

        public IReadOnlyDictionary<string, long> LocalSequences => localSequenceByAggregate;

        public void RestoreLocalSequence(string aggregateId, long sequence)
        {
            if (string.IsNullOrWhiteSpace(aggregateId) || sequence < 0)
            {
                return;
            }

            localSequenceByAggregate[aggregateId] = sequence;
        }

        public DomainEvent Create(string aggregateId, string type, string payloadJson)
        {
            return Create(aggregateId, type, payloadJson, DomainEventId.NewUlid(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        public DomainEvent Create(string aggregateId, string type, string payloadJson, string eventId, long occurredAtUnixMs)
        {
            string resolvedAggregateId = string.IsNullOrWhiteSpace(aggregateId) ? "world" : aggregateId;
            return new DomainEvent
            {
                EventId = string.IsNullOrWhiteSpace(eventId) ? DomainEventId.NewUlid(occurredAtUnixMs) : eventId,
                WorldId = worldId,
                AggregateId = resolvedAggregateId,
                LocalSequence = NextLocalSequence(resolvedAggregateId),
                Type = type ?? string.Empty,
                Payload = ByteString.CopyFromUtf8(payloadJson ?? "{}"),
                OccurredAtUnixMs = occurredAtUnixMs
            };
        }

        private long NextLocalSequence(string aggregateId)
        {
            if (!localSequenceByAggregate.TryGetValue(aggregateId, out long current))
            {
                current = 0;
            }

            current++;
            localSequenceByAggregate[aggregateId] = current;
            return current;
        }
    }
}

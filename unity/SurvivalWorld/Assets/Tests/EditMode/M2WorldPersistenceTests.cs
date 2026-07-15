using System.Linq;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using Survival.V1;
using SurvivalWorld.World;

namespace SurvivalWorld.Tests
{
    public sealed class M2WorldPersistenceTests
    {
        [Test]
        public void SnapshotChecksumUsesSha256Hex()
        {
            string checksum = SnapshotChecksum.ComputeSha256Hex(Encoding.UTF8.GetBytes("abc"));

            Assert.AreEqual("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", checksum);
        }

        [Test]
        public void WorldRuntimeAppliesEventTailInLocalSequenceOrderAndSkipsDuplicates()
        {
            WorldRuntimeState runtime = new WorldRuntimeState();
            runtime.RestoreSnapshot(null, 10);
            DomainEvent first = Event("evt-1", 1);
            DomainEvent second = Event("evt-2", 2);

            int applied = runtime.ApplyEventTail(new[] { second, first, first });

            Assert.AreEqual(2, applied);
            Assert.AreEqual("evt-1", runtime.AppliedEvents[0].EventId);
            Assert.AreEqual("evt-2", runtime.AppliedEvents[1].EventId);
            Assert.AreEqual(10, runtime.CurrentSequence);
        }

        [Test]
        public void WorldBootstrapClientRestoresSnapshotAndTail()
        {
            var gateway = new FakeWorldDataGateway(new LoadBootstrapResponse
            {
                SnapshotId = "snapshot-1",
                Sequence = 7
            });
            gateway.Response.EventTail.Add(Event("evt-2", 2));
            gateway.Response.EventTail.Add(Event("evt-1", 1));
            WorldRuntimeState runtime = new WorldRuntimeState();
            var client = new WorldBootstrapClient(gateway, runtime);

            WorldBootstrapResult result = client.LoadAsync("world", "build", CancellationToken.None).GetAwaiter().GetResult();

            Assert.IsTrue(result.Ok);
            Assert.AreEqual(2, result.EventsApplied);
            Assert.AreEqual(7, runtime.CurrentSequence);
            Assert.AreEqual("evt-1", runtime.AppliedEvents.First().EventId);
        }

        private static DomainEvent Event(string eventId, long localSequence)
        {
            return new DomainEvent
            {
                EventId = eventId,
                WorldId = "world",
                AggregateId = "owner",
                LocalSequence = localSequence,
                Type = "test",
                OccurredAtUnixMs = 1
            };
        }

        private sealed class FakeWorldDataGateway : IWorldDataGateway
        {
            public FakeWorldDataGateway(LoadBootstrapResponse response)
            {
                Response = response;
            }

            public LoadBootstrapResponse Response { get; }

            public UniTask<LoadBootstrapResponse> LoadBootstrapAsync(string worldId, string serverBuild, CancellationToken cancellationToken)
            {
                return UniTask.FromResult(Response);
            }

            public UniTask<AppendEventsResponse> AppendEventsAsync(string serverId, DomainEvent[] events, CancellationToken cancellationToken)
            {
                var response = new AppendEventsResponse();
                for (int i = 0; i < events.Length; i++)
                {
                    response.Results.Add(ResultStatus.Ok);
                }

                return UniTask.FromResult(response);
            }

            public UniTask<SaveSnapshotResponse> SaveSnapshotAsync(string worldId, long sequence, string checksum, byte[] payload, CancellationToken cancellationToken)
            {
                return UniTask.FromResult(new SaveSnapshotResponse { SnapshotId = checksum });
            }
        }
    }
}


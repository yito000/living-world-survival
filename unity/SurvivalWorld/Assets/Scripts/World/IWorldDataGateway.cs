using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Grpc.Core;
using Survival.V1;

namespace SurvivalWorld.World
{
    public interface IWorldDataGateway
    {
        UniTask<LoadBootstrapResponse> LoadBootstrapAsync(string worldId, string serverBuild, CancellationToken cancellationToken);
        UniTask<AppendEventsResponse> AppendEventsAsync(string serverId, DomainEvent[] events, CancellationToken cancellationToken);
        UniTask<SaveSnapshotResponse> SaveSnapshotAsync(string worldId, long sequence, string checksum, byte[] payload, CancellationToken cancellationToken);
    }

    public sealed class GeneratedWorldDataGateway : IWorldDataGateway
    {
        private readonly WorldDataService.WorldDataServiceClient client;
        private readonly string serviceSecret;

        public GeneratedWorldDataGateway(WorldDataService.WorldDataServiceClient client, string serviceSecret)
        {
            this.client = client ?? throw new ArgumentNullException(nameof(client));
            this.serviceSecret = serviceSecret ?? string.Empty;
        }

        public async UniTask<LoadBootstrapResponse> LoadBootstrapAsync(string worldId, string serverBuild, CancellationToken cancellationToken)
        {
            return await client.LoadBootstrapAsync(new LoadBootstrapRequest
            {
                WorldId = worldId,
                ServerBuild = serverBuild
            }, CreateHeaders(), cancellationToken: cancellationToken).ResponseAsync.AsUniTask();
        }

        public async UniTask<AppendEventsResponse> AppendEventsAsync(string serverId, DomainEvent[] events, CancellationToken cancellationToken)
        {
            var request = new AppendEventsRequest
            {
                ServerId = serverId
            };
            request.Events.Add(events ?? Array.Empty<DomainEvent>());
            return await client.AppendEventsAsync(request, CreateHeaders(), cancellationToken: cancellationToken).ResponseAsync.AsUniTask();
        }

        public async UniTask<SaveSnapshotResponse> SaveSnapshotAsync(string worldId, long sequence, string checksum, byte[] payload, CancellationToken cancellationToken)
        {
            return await client.SaveSnapshotAsync(new SaveSnapshotRequest
            {
                WorldId = worldId,
                Sequence = sequence,
                Checksum = checksum ?? string.Empty,
                Payload = Google.Protobuf.ByteString.CopyFrom(payload ?? Array.Empty<byte>())
            }, CreateHeaders(), cancellationToken: cancellationToken).ResponseAsync.AsUniTask();
        }

        private Metadata CreateHeaders()
        {
            if (string.IsNullOrWhiteSpace(serviceSecret))
            {
                return null;
            }

            return new Metadata { { "x-service-secret", serviceSecret } };
        }
    }
}

using System.Threading;
using Cysharp.Threading.Tasks;
using Survival.V1;

namespace SurvivalWorld.Server
{
    public readonly struct MatchmakingGatewayResult
    {
        private MatchmakingGatewayResult(bool ok, string error)
        {
            Ok = ok;
            Error = error ?? string.Empty;
        }

        public bool Ok { get; }
        public string Error { get; }

        public static MatchmakingGatewayResult Success()
        {
            return new MatchmakingGatewayResult(true, string.Empty);
        }

        public static MatchmakingGatewayResult Failure(string error)
        {
            return new MatchmakingGatewayResult(false, error);
        }
    }

    public interface IMatchmakingGateway
    {
        UniTask<MatchmakingGatewayResult> RedeemJoinTicketAsync(string serverId, string ticket, CancellationToken cancellationToken);
        UniTask<MatchmakingGatewayResult> RegisterServerAsync(string serverId, string worldId, string buildId, string endpoint, int capacity, CancellationToken cancellationToken);
        UniTask<MatchmakingGatewayResult> HeartbeatAsync(string serverId, int players, bool ready, int tickMilliseconds, CancellationToken cancellationToken);
        UniTask<MatchmakingGatewayResult> MarkDrainingAsync(string serverId, CancellationToken cancellationToken);
    }

    public sealed class UnavailableMatchmakingGateway : IMatchmakingGateway
    {
        public static readonly UnavailableMatchmakingGateway Instance = new UnavailableMatchmakingGateway();

        private UnavailableMatchmakingGateway()
        {
        }

        public UniTask<MatchmakingGatewayResult> RedeemJoinTicketAsync(string serverId, string ticket, CancellationToken cancellationToken)
        {
            return UniTask.FromResult(MatchmakingGatewayResult.Failure("Matchmaking gateway is not configured."));
        }

        public UniTask<MatchmakingGatewayResult> RegisterServerAsync(string serverId, string worldId, string buildId, string endpoint, int capacity, CancellationToken cancellationToken)
        {
            return UniTask.FromResult(MatchmakingGatewayResult.Failure("Matchmaking gateway is not configured."));
        }

        public UniTask<MatchmakingGatewayResult> HeartbeatAsync(string serverId, int players, bool ready, int tickMilliseconds, CancellationToken cancellationToken)
        {
            return UniTask.FromResult(MatchmakingGatewayResult.Failure("Matchmaking gateway is not configured."));
        }

        public UniTask<MatchmakingGatewayResult> MarkDrainingAsync(string serverId, CancellationToken cancellationToken)
        {
            return UniTask.FromResult(MatchmakingGatewayResult.Failure("Matchmaking gateway is not configured."));
        }
    }

    public sealed class GeneratedMatchmakingGateway : IMatchmakingGateway
    {
        private readonly MatchmakingService.MatchmakingServiceClient client;

        public GeneratedMatchmakingGateway(MatchmakingService.MatchmakingServiceClient client)
        {
            this.client = client;
        }

        public async UniTask<MatchmakingGatewayResult> RedeemJoinTicketAsync(string serverId, string ticket, CancellationToken cancellationToken)
        {
            var response = await client.RedeemJoinTicketAsync(new RedeemJoinTicketRequest
            {
                ServerId = serverId,
                Ticket = ticket
            }, cancellationToken: cancellationToken).ResponseAsync.AsUniTask();

            return response.Ok ? MatchmakingGatewayResult.Success() : MatchmakingGatewayResult.Failure(response.Error);
        }

        public async UniTask<MatchmakingGatewayResult> RegisterServerAsync(string serverId, string worldId, string buildId, string endpoint, int capacity, CancellationToken cancellationToken)
        {
            var response = await client.RegisterServerAsync(new RegisterServerRequest
            {
                ServerId = serverId,
                WorldId = worldId,
                BuildId = buildId,
                Endpoint = endpoint,
                Capacity = capacity
            }, cancellationToken: cancellationToken).ResponseAsync.AsUniTask();

            return response.Ok ? MatchmakingGatewayResult.Success() : MatchmakingGatewayResult.Failure("RegisterServer returned ok=false.");
        }

        public async UniTask<MatchmakingGatewayResult> HeartbeatAsync(string serverId, int players, bool ready, int tickMilliseconds, CancellationToken cancellationToken)
        {
            var response = await client.HeartbeatAsync(new HeartbeatRequest
            {
                ServerId = serverId,
                Players = players,
                Ready = ready,
                TickMs = tickMilliseconds
            }, cancellationToken: cancellationToken).ResponseAsync.AsUniTask();

            return response.Ok ? MatchmakingGatewayResult.Success() : MatchmakingGatewayResult.Failure("Heartbeat returned ok=false.");
        }

        public async UniTask<MatchmakingGatewayResult> MarkDrainingAsync(string serverId, CancellationToken cancellationToken)
        {
            var response = await client.MarkDrainingAsync(new MarkDrainingRequest
            {
                ServerId = serverId
            }, cancellationToken: cancellationToken).ResponseAsync.AsUniTask();

            return response.Ok ? MatchmakingGatewayResult.Success() : MatchmakingGatewayResult.Failure("MarkDraining returned ok=false.");
        }
    }
}

using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Grpc.Core;
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
        private readonly string serviceSecret;

        public GeneratedMatchmakingGateway(MatchmakingService.MatchmakingServiceClient client, string serviceSecret = null)
        {
            this.client = client ?? throw new ArgumentNullException(nameof(client));
            this.serviceSecret = serviceSecret ?? string.Empty;
        }

        public UniTask<MatchmakingGatewayResult> RedeemJoinTicketAsync(string serverId, string ticket, CancellationToken cancellationToken)
        {
            return ExecuteAsync(
                headers => client.RedeemJoinTicketAsync(new RedeemJoinTicketRequest
                {
                    ServerId = serverId,
                    Ticket = ticket
                }, headers, cancellationToken: cancellationToken),
                response => response.Ok ? MatchmakingGatewayResult.Success() : MatchmakingGatewayResult.Failure(response.Error),
                cancellationToken);
        }

        public UniTask<MatchmakingGatewayResult> RegisterServerAsync(string serverId, string worldId, string buildId, string endpoint, int capacity, CancellationToken cancellationToken)
        {
            return ExecuteAsync(
                headers => client.RegisterServerAsync(new RegisterServerRequest
                {
                    ServerId = serverId,
                    WorldId = worldId,
                    BuildId = buildId,
                    Endpoint = endpoint,
                    Capacity = capacity
                }, headers, cancellationToken: cancellationToken),
                response => response.Ok ? MatchmakingGatewayResult.Success() : MatchmakingGatewayResult.Failure("RegisterServer returned ok=false."),
                cancellationToken);
        }

        public UniTask<MatchmakingGatewayResult> HeartbeatAsync(string serverId, int players, bool ready, int tickMilliseconds, CancellationToken cancellationToken)
        {
            return ExecuteAsync(
                headers => client.HeartbeatAsync(new HeartbeatRequest
                {
                    ServerId = serverId,
                    Players = players,
                    Ready = ready,
                    TickMs = tickMilliseconds
                }, headers, cancellationToken: cancellationToken),
                response => response.Ok ? MatchmakingGatewayResult.Success() : MatchmakingGatewayResult.Failure("Heartbeat returned ok=false."),
                cancellationToken);
        }

        public UniTask<MatchmakingGatewayResult> MarkDrainingAsync(string serverId, CancellationToken cancellationToken)
        {
            return ExecuteAsync(
                headers => client.MarkDrainingAsync(new MarkDrainingRequest
                {
                    ServerId = serverId
                }, headers, cancellationToken: cancellationToken),
                response => response.Ok ? MatchmakingGatewayResult.Success() : MatchmakingGatewayResult.Failure("MarkDraining returned ok=false."),
                cancellationToken);
        }

        private async UniTask<MatchmakingGatewayResult> ExecuteAsync<TResponse>(Func<Metadata, AsyncUnaryCall<TResponse>> call, Func<TResponse, MatchmakingGatewayResult> map, CancellationToken cancellationToken)
        {
            try
            {
                TResponse response = await call(CreateHeaders()).ResponseAsync.AsUniTask();
                return map(response);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (RpcException ex)
            {
                return MatchmakingGatewayResult.Failure("grpc_" + ex.StatusCode + ": " + ex.Status.Detail);
            }
            catch (Exception ex)
            {
                return MatchmakingGatewayResult.Failure("grpc_error: " + ex.Message);
            }
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
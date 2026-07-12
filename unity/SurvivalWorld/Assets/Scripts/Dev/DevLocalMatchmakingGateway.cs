using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using SurvivalWorld.Net;
using SurvivalWorld.Server;

namespace SurvivalWorld.Dev
{
    public sealed class DevLocalMatchmakingGateway : IMatchmakingGateway
    {
        private readonly IJoinTicketVerifier verifier;
        private readonly string expectedBuildId;
        private readonly string publicKey;
        private readonly HashSet<string> usedTicketIds = new HashSet<string>();
        private readonly Dictionary<string, DevServerState> servers = new Dictionary<string, DevServerState>();

        public DevLocalMatchmakingGateway(string expectedBuildId, string publicKey, IJoinTicketVerifier verifier = null)
        {
            this.expectedBuildId = expectedBuildId ?? string.Empty;
            this.publicKey = publicKey ?? string.Empty;
            this.verifier = verifier ?? new JwsEd25519JoinTicketVerifier();
        }

        public UniTask<MatchmakingGatewayResult> RedeemJoinTicketAsync(string serverId, string ticket, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var context = new JoinTicketVerificationContext(publicKey, serverId, expectedBuildId, IsCharacterAllowed, nowMs);
            JoinTicketValidationResult result = verifier.Verify(ticket, context);
            if (!result.Accepted)
            {
                return UniTask.FromResult(MatchmakingGatewayResult.Failure(ToGatewayError(result)));
            }

            if (!usedTicketIds.Add(result.Claims.TicketId))
            {
                return UniTask.FromResult(MatchmakingGatewayResult.Failure("join_ticket_reused"));
            }

            return UniTask.FromResult(MatchmakingGatewayResult.Success());
        }

        public UniTask<MatchmakingGatewayResult> RegisterServerAsync(string serverId, string worldId, string buildId, string endpoint, int capacity, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            servers[serverId ?? string.Empty] = new DevServerState(worldId, buildId, endpoint, capacity, 0, true);
            return UniTask.FromResult(MatchmakingGatewayResult.Success());
        }

        public UniTask<MatchmakingGatewayResult> HeartbeatAsync(string serverId, int players, bool ready, int tickMilliseconds, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string key = serverId ?? string.Empty;
            if (servers.TryGetValue(key, out DevServerState state))
            {
                servers[key] = new DevServerState(state.WorldId, state.BuildId, state.Endpoint, state.Capacity, players, ready);
            }

            return UniTask.FromResult(MatchmakingGatewayResult.Success());
        }

        public UniTask<MatchmakingGatewayResult> MarkDrainingAsync(string serverId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string key = serverId ?? string.Empty;
            if (servers.TryGetValue(key, out DevServerState state))
            {
                servers[key] = new DevServerState(state.WorldId, state.BuildId, state.Endpoint, state.Capacity, state.Players, false);
            }

            return UniTask.FromResult(MatchmakingGatewayResult.Success());
        }

        private static bool IsCharacterAllowed(string characterId)
        {
            return !string.IsNullOrWhiteSpace(characterId);
        }

        private static string ToGatewayError(JoinTicketValidationResult result)
        {
            switch (result.Reason)
            {
                case JoinTicketRejectionReason.Expired:
                    return "join_ticket_expired";
                case JoinTicketRejectionReason.ServerMismatch:
                    return "join_ticket_server_mismatch";
                case JoinTicketRejectionReason.BuildMismatch:
                    return "join_ticket_build_mismatch";
                case JoinTicketRejectionReason.InvalidCharacter:
                    return "join_ticket_invalid_character";
                case JoinTicketRejectionReason.SignatureInvalid:
                    return "join_ticket_signature_invalid";
                default:
                    return string.IsNullOrWhiteSpace(result.Message) ? "join_ticket_invalid" : result.Message;
            }
        }

        private readonly struct DevServerState
        {
            public DevServerState(string worldId, string buildId, string endpoint, int capacity, int players, bool ready)
            {
                WorldId = worldId ?? string.Empty;
                BuildId = buildId ?? string.Empty;
                Endpoint = endpoint ?? string.Empty;
                Capacity = capacity;
                Players = players;
                Ready = ready;
            }

            public string WorldId { get; }
            public string BuildId { get; }
            public string Endpoint { get; }
            public int Capacity { get; }
            public int Players { get; }
            public bool Ready { get; }
        }
    }
}
using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using FishNet.Authenticating;
using FishNet.Connection;
using FishNet.Managing.Logging;
using FishNet.Transporting;
using SurvivalWorld.Server;
using UnityEngine;

namespace SurvivalWorld.Net
{
    public sealed class JoinTicketAuthenticator : Authenticator
    {
        [SerializeField] private string expectedServerId = "00000000-0000-0000-0000-000000000101";
        [SerializeField] private string expectedBuildId = "dev-local";
        [TextArea(3, 6)]
        [SerializeField] private string joinTicketPublicKey = string.Empty;

        private readonly IJoinTicketVerifier verifier = new JwsEd25519JoinTicketVerifier();
        private IMatchmakingGateway matchmakingGateway = UnavailableMatchmakingGateway.Instance;

        public override event Action<NetworkConnection, bool> OnAuthenticationResult;

        public override void InitializeOnce(FishNet.Managing.NetworkManager networkManager)
        {
            base.InitializeOnce(networkManager);
            NetworkManager.ServerManager.RegisterBroadcast<JoinTicketBroadcast>(OnJoinTicketBroadcast, requireAuthentication: false);
        }

        public void Configure(string serverId, string buildId, string publicKey, IMatchmakingGateway gateway)
        {
            expectedServerId = serverId;
            expectedBuildId = buildId;
            joinTicketPublicKey = publicKey;
            matchmakingGateway = gateway ?? UnavailableMatchmakingGateway.Instance;
        }

        private void OnJoinTicketBroadcast(NetworkConnection connection, JoinTicketBroadcast message, FishNet.Transporting.Channel channel)
        {
            Debug.Log("Join ticket broadcast received from connection " + connection.ClientId + ", length=" + (message.Ticket == null ? 0 : message.Ticket.Length) + ".");
            AuthenticateAsync(connection, message.Ticket, destroyCancellationToken).Forget();
        }

        private async UniTask AuthenticateAsync(NetworkConnection connection, string ticket, CancellationToken cancellationToken)
        {
            try
            {
                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var context = new JoinTicketVerificationContext(joinTicketPublicKey, expectedServerId, expectedBuildId, IsCharacterAllowed, nowMs);
                JoinTicketValidationResult localResult = verifier.Verify(ticket, context);
                if (!localResult.Accepted)
                {
                    Reject(connection, localResult.Reason, localResult.Message);
                    return;
                }

                MatchmakingGatewayResult redeem = await matchmakingGateway.RedeemJoinTicketAsync(expectedServerId, ticket, cancellationToken);
                await UniTask.SwitchToMainThread(cancellationToken);
                if (!redeem.Ok)
                {
                    Reject(connection, JoinTicketRejectionReason.RedeemRejected, redeem.Error);
                    return;
                }

                Debug.Log($"Join ticket accepted for connection {connection.ClientId}, account={localResult.Claims.AccountId}, character={localResult.Claims.CharacterId}.");
                OnAuthenticationResult?.Invoke(connection, true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                await UniTask.SwitchToMainThread();
                Debug.LogException(ex);
                Reject(connection, JoinTicketRejectionReason.RedeemRejected, ex.Message);
            }
        }

        private static bool IsCharacterAllowed(string characterId)
        {
            return !string.IsNullOrWhiteSpace(characterId);
        }

        private void Reject(NetworkConnection connection, JoinTicketRejectionReason reason, string message)
        {
            Debug.LogWarning($"Join ticket rejected for connection {connection.ClientId}: {reason} {message}");
            OnAuthenticationResult?.Invoke(connection, false);
            connection.Kick(FishNet.Managing.Server.KickReason.UnexpectedProblem, LoggingType.Common, message);
        }
    }
}

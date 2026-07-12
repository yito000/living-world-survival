using System.Threading;
using Cysharp.Threading.Tasks;
using FishNet.Managing;
using SurvivalWorld.Config;
using SurvivalWorld.Net;
using UnityEngine;

namespace SurvivalWorld.Server
{
    public sealed class ServerBootstrap : MonoBehaviour
    {
        [SerializeField] private SurvivalRuntimeConfig config;
        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private JoinTicketAuthenticator authenticator;

        private readonly IMatchmakingGateway matchmakingGateway = UnavailableMatchmakingGateway.Instance;
        private CancellationTokenSource lifetime;

        private void Awake()
        {
            if (networkManager == null)
            {
                networkManager = FindFirstObjectByType<NetworkManager>();
            }

            if (authenticator == null && networkManager != null)
            {
                authenticator = networkManager.GetComponent<JoinTicketAuthenticator>();
            }

            if (config != null && authenticator != null)
            {
                authenticator.Configure(config.ServerId, config.BuildId, config.JoinTicketPublicKey, matchmakingGateway);
            }
        }

        private void Start()
        {
            if (Application.isBatchMode)
            {
                lifetime = CancellationTokenSource.CreateLinkedTokenSource(destroyCancellationToken);
                RunServerAsync(lifetime.Token).Forget();
            }
        }

        private async UniTask RunServerAsync(CancellationToken cancellationToken)
        {
            if (networkManager == null || config == null)
            {
                Debug.LogWarning("ServerBootstrap requires NetworkManager and SurvivalRuntimeConfig.");
                return;
            }

            networkManager.ServerManager.StartConnection(config.ServerPort);
            MatchmakingGatewayResult register = await matchmakingGateway.RegisterServerAsync(config.ServerId, config.WorldId, config.BuildId, config.ServerEndpoint, config.ServerCapacity, cancellationToken);
            if (!register.Ok)
            {
                Debug.LogWarning("RegisterServer failed: " + register.Error);
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                int playerCount = networkManager.ServerManager.Clients.Count;
                MatchmakingGatewayResult heartbeat = await matchmakingGateway.HeartbeatAsync(config.ServerId, playerCount, true, config.TickMilliseconds, cancellationToken);
                if (!heartbeat.Ok)
                {
                    Debug.LogWarning("Heartbeat failed: " + heartbeat.Error);
                }

                await UniTask.Delay(System.TimeSpan.FromSeconds(config.HeartbeatSeconds), cancellationToken: cancellationToken);
            }
        }

        private void OnDestroy()
        {
            lifetime?.Cancel();
            lifetime?.Dispose();
            lifetime = null;

            if (config != null)
            {
                matchmakingGateway.MarkDrainingAsync(config.ServerId, CancellationToken.None).Forget();
            }
        }
    }
}

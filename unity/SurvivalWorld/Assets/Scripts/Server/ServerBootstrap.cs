using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Object;
using FishNet.Transporting;
using SurvivalWorld.Config;
using SurvivalWorld.Dev;
using SurvivalWorld.Net;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;

namespace SurvivalWorld.Server
{
    public sealed class ServerBootstrap : MonoBehaviour
    {
        [SerializeField] private SurvivalRuntimeConfig config;
        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private JoinTicketAuthenticator authenticator;
        [SerializeField] private NetworkObject playerPrefab;
        [SerializeField] private string worldSceneName = "World_MVP";

        private readonly HashSet<int> authenticatedClientIds = new HashSet<int>();
        private readonly Dictionary<int, NetworkObject> spawnedPlayers = new Dictionary<int, NetworkObject>();
        private IMatchmakingGateway matchmakingGateway = UnavailableMatchmakingGateway.Instance;
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

            string publicKey = config == null ? string.Empty : config.JoinTicketPublicKey;
            matchmakingGateway = CreateMatchmakingGateway(ref publicKey);

            if (config != null && authenticator != null)
            {
                authenticator.Configure(config.ServerId, config.BuildId, publicKey, matchmakingGateway);
                authenticator.OnAuthenticationResult += OnAuthenticationResult;
            }

            if (networkManager != null)
            {
                networkManager.ServerManager.OnRemoteConnectionState += OnRemoteConnectionState;
            }

            UnitySceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void Start()
        {
            if (Application.isBatchMode || ShouldStartDevLocalServerInEditor())
            {
                lifetime = CancellationTokenSource.CreateLinkedTokenSource(destroyCancellationToken);
                RunServerAsync(lifetime.Token).Forget();
            }
        }

        private IMatchmakingGateway CreateMatchmakingGateway(ref string publicKey)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (config != null && config.DevLocalMode)
            {
                var issuer = new DevLocalJoinTicketIssuer();
                publicKey = issuer.PublicKey;
                return new DevLocalMatchmakingGateway(config.BuildId, publicKey);
            }
#endif
            return UnavailableMatchmakingGateway.Instance;
        }

        private bool ShouldStartDevLocalServerInEditor()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            return Application.isEditor && config != null && config.DevLocalMode && config.AutoStartLocalServerInEditor;
#else
            return false;
#endif
        }

        private bool ShouldUseDevLocalSpawn()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            return config != null && config.DevLocalMode;
#else
            return false;
#endif
        }

        private async UniTask RunServerAsync(CancellationToken cancellationToken)
        {
            if (networkManager == null || config == null)
            {
                Debug.LogWarning("ServerBootstrap requires NetworkManager and SurvivalRuntimeConfig.");
                return;
            }

            try
            {
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

                    await UniTask.Delay(TimeSpan.FromSeconds(config.HeartbeatSeconds), cancellationToken: cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private void OnAuthenticationResult(NetworkConnection connection, bool authenticated)
        {
            if (!ShouldUseDevLocalSpawn() || connection == null)
            {
                return;
            }

            if (authenticated)
            {
                authenticatedClientIds.Add(connection.ClientId);
                TrySpawnDevPlayer(connection);
            }
        }

        private void OnRemoteConnectionState(NetworkConnection connection, RemoteConnectionStateArgs args)
        {
            if (connection == null || args.ConnectionState != RemoteConnectionState.Stopped)
            {
                return;
            }

            authenticatedClientIds.Remove(connection.ClientId);
            spawnedPlayers.Remove(connection.ClientId);
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!ShouldUseDevLocalSpawn() || scene.name != worldSceneName || networkManager == null)
            {
                return;
            }

            foreach (int clientId in authenticatedClientIds)
            {
                if (networkManager.ServerManager.Clients.TryGetValue(clientId, out NetworkConnection connection))
                {
                    TrySpawnDevPlayer(connection);
                }
            }
        }

        private void TrySpawnDevPlayer(NetworkConnection connection)
        {
            if (connection == null || !connection.IsAuthenticated || spawnedPlayers.ContainsKey(connection.ClientId))
            {
                return;
            }

            if (UnitySceneManager.GetActiveScene().name != worldSceneName)
            {
                return;
            }

            if (networkManager == null || playerPrefab == null)
            {
                Debug.LogWarning("ServerBootstrap requires a PlayerCharacter NetworkObject prefab for Dev Local spawn.");
                return;
            }

            NetworkObject player = networkManager.GetPooledInstantiated(playerPrefab, playerPrefab.transform.position, playerPrefab.transform.rotation, true);
            if (player == null)
            {
                Debug.LogWarning("Failed to instantiate Dev Local player prefab.");
                return;
            }

            networkManager.ServerManager.Spawn(player, connection, UnitySceneManager.GetActiveScene());
            networkManager.SceneManager.AddOwnerToDefaultScene(player);
            spawnedPlayers[connection.ClientId] = player;
        }

        private void OnDestroy()
        {
            if (authenticator != null)
            {
                authenticator.OnAuthenticationResult -= OnAuthenticationResult;
            }

            if (networkManager != null)
            {
                networkManager.ServerManager.OnRemoteConnectionState -= OnRemoteConnectionState;
            }

            UnitySceneManager.sceneLoaded -= OnSceneLoaded;
            lifetime?.Cancel();
            lifetime?.Dispose();
            lifetime = null;

            if (config != null && matchmakingGateway != null)
            {
                matchmakingGateway.MarkDrainingAsync(config.ServerId, CancellationToken.None).Forget();
            }
        }
    }
}
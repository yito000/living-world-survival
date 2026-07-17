using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Cysharp.Threading.Tasks;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Object;
using FishNet.Transporting;
using Grpc.Core;
using Survival.V1;
using SurvivalWorld.Config;
using SurvivalWorld.Dev;
using SurvivalWorld.Inventory;
using SurvivalWorld.Items;
using SurvivalWorld.Net;
using SurvivalWorld.Server.AI;
using SurvivalWorld.Shared.MasterData;
using SurvivalWorld.World;
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
        private ChannelBase authGrpcChannel;
        private ChannelBase worldDataGrpcChannel;
        private ChannelBase economyGrpcChannel;
        private WorldRuntimeState worldRuntimeState;
        private RuntimePersistenceAgent persistenceAgent;
        private InventoryRuntimeService inventoryRuntimeService;
        private SurvivalWorld.Economy.BuyerRegistry buyerRegistry;
        private SurvivalWorld.Economy.BuyerPurchaseHandler buyerPurchaseHandler;
        private SurvivalWorld.Economy.BuyerSpawnController buyerSpawnController;
        private AIActorSystem aiActorSystem;
        private bool serverBootstrapActive;

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

            if (!ShouldRunServerBootstrap())
            {
                return;
            }

            serverBootstrapActive = true;
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
            if (!serverBootstrapActive)
            {
                return;
            }

            lifetime = CancellationTokenSource.CreateLinkedTokenSource(destroyCancellationToken);
            RunServerAsync(lifetime.Token).Forget();
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
            if (config == null)
            {
                return UnavailableMatchmakingGateway.Instance;
            }

            if (string.IsNullOrWhiteSpace(publicKey))
            {
                Debug.LogWarning("JoinTicketPublicKey is empty; real backend tickets will be rejected by local verification.");
            }

            if (!EndpointParser.TryParse(config.AuthGrpcEndpoint, out var endpoint))
            {
                Debug.LogWarning("Invalid Auth gRPC endpoint: " + config.AuthGrpcEndpoint);
                return UnavailableMatchmakingGateway.Instance;
            }

            authGrpcChannel = CreateGrpcChannel(endpoint, "Auth");
            if (authGrpcChannel == null)
            {
                return UnavailableMatchmakingGateway.Instance;
            }

            var client = new MatchmakingService.MatchmakingServiceClient(authGrpcChannel);
            return new GeneratedMatchmakingGateway(client, config.AuthGrpcSharedSecret);
        }

        private static ChannelBase CreateGrpcChannel(ServerEndpoint endpoint, string label)
        {
            Type channelType = Type.GetType("Grpc.Core.Channel, Grpc.Core");
            if (channelType == null)
            {
                Debug.LogWarning("Grpc.Core runtime is not loaded. Run NuGetForUnity restore for Grpc.Core.");
                return null;
            }

            try
            {
                return (ChannelBase)Activator.CreateInstance(channelType, endpoint.Host, (int)endpoint.Port, ChannelCredentials.Insecure);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("Failed to create " + label + " gRPC channel: " + ex);
                if (ex.InnerException != null)
                {
                    Debug.LogWarning(label + " gRPC channel inner exception: " + ex.InnerException);
                }

                return null;
            }
        }

        private bool ShouldRunServerBootstrap()
        {
            return Application.isBatchMode || ShouldStartServerInEditor();
        }

        private bool ShouldStartServerInEditor()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            return Application.isEditor && config != null && config.DevLocalMode && config.AutoStartLocalServerInEditor;
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
                bool started = networkManager.ServerManager.StartConnection(config.ServerPort);
                if (!started)
                {
                    Debug.LogWarning("FishNet server failed to start on port " + config.ServerPort + ".");
                    return;
                }

                await LoadWorldSceneIfNeededAsync(cancellationToken);
                WorldBootstrapResult worldBootstrap = await BootstrapWorldAsync(cancellationToken);
                bool ready = worldBootstrap.Ok || config.DevLocalMode;
                if (!worldBootstrap.Ok)
                {
                    Debug.LogWarning("World bootstrap failed: " + worldBootstrap.Error);
                }
                else if (persistenceAgent != null)
                {
                    persistenceAgent.RunAsync(
                        TimeSpan.FromMilliseconds(config.OutboxFlushIntervalMilliseconds),
                        TimeSpan.FromSeconds(config.SnapshotIntervalSeconds),
                        cancellationToken).Forget();
                }

                if (aiActorSystem != null)
                {
                    aiActorSystem.RunAsync(
                        TimeSpan.FromMilliseconds(config.TickMilliseconds),
                        TimeSpan.FromSeconds(config.SnapshotIntervalSeconds),
                        cancellationToken).Forget();
                }

                MatchmakingGatewayResult register = await matchmakingGateway.RegisterServerAsync(config.ServerId, config.WorldId, config.BuildId, config.ServerEndpoint, config.ServerCapacity, cancellationToken);
                if (!register.Ok)
                {
                    Debug.LogWarning("RegisterServer failed: " + register.Error);
                }

                while (!cancellationToken.IsCancellationRequested)
                {
                    int playerCount = networkManager.ServerManager.Clients.Count;
                    MatchmakingGatewayResult heartbeat = await matchmakingGateway.HeartbeatAsync(config.ServerId, playerCount, ready, config.TickMilliseconds, cancellationToken);
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

        private async UniTask<WorldBootstrapResult> BootstrapWorldAsync(CancellationToken cancellationToken)
        {
            worldRuntimeState = new WorldRuntimeState();
            IWorldDataGateway worldDataGateway = CreateWorldDataGateway();
            if (worldDataGateway == null)
            {
                if (config != null && config.DevLocalMode)
                {
                    ConfigureInventoryRuntime(NullInventoryEventSink.Instance);
                    ConfigureEconomyRuntime();
                    ConfigureAIActorSystem(NullActorStateGateway.Instance);
                    return WorldBootstrapResult.Success(0, 0);
                }

                return WorldBootstrapResult.Failure("WorldData gateway is not configured.");
            }

            var bootstrapClient = new WorldBootstrapClient(worldDataGateway, worldRuntimeState);
            WorldBootstrapResult result = await bootstrapClient.LoadAsync(config.WorldId, config.BuildId, cancellationToken);
            if (result.Ok)
            {
                persistenceAgent = new RuntimePersistenceAgent(worldDataGateway, worldRuntimeState, config.ServerId, config.WorldId);
                ConfigureInventoryRuntime(persistenceAgent);
                ConfigureEconomyRuntime();
                ConfigureAIActorSystem(CreateActorStateGateway());
            }

            return result;
        }

        private void ConfigureInventoryRuntime(IInventoryEventSink eventSink)
        {
            string configuredWorldId = config == null ? string.Empty : config.WorldId;
            inventoryRuntimeService = new InventoryRuntimeService(ItemDefinitionCatalog.CreateMvpCatalog(), eventSink ?? NullInventoryEventSink.Instance, configuredWorldId);
        }


        private void ConfigureEconomyRuntime()
        {
            if (inventoryRuntimeService == null)
            {
                buyerRegistry = null;
                buyerPurchaseHandler = null;
                buyerSpawnController = null;
                return;
            }

            SurvivalWorld.Economy.IEconomyClient economyClient = CreateEconomyClient();
            buyerRegistry = new SurvivalWorld.Economy.BuyerRegistry();
            var reconciler = new SurvivalWorld.Economy.InventoryReconciler(inventoryRuntimeService, SurvivalWorld.Economy.NullInventorySnapshotProvider.Instance);
            var protocol = new SurvivalWorld.Economy.PurchaseProtocol(economyClient, inventoryRuntimeService, reconciler);
            string configuredWorldId = config == null ? string.Empty : config.WorldId;
            buyerPurchaseHandler = new SurvivalWorld.Economy.BuyerPurchaseHandler(buyerRegistry, inventoryRuntimeService, protocol, configuredWorldId);
            buyerSpawnController = new SurvivalWorld.Economy.BuyerSpawnController(economyClient, buyerRegistry, configuredWorldId, "mvp-region", "mvp-buyer-stock", 10000);
        }
        private void ConfigureAIActorSystem(IActorStateGateway actorStateGateway)
        {
            string configuredServerId = config == null ? string.Empty : config.ServerId;
            string configuredWorldId = config == null ? string.Empty : config.WorldId;
            aiActorSystem = AIActorSystem.CreateDefault(configuredServerId, configuredWorldId, M3ItemDefinitions.CreateCatalog(), actorStateGateway ?? NullActorStateGateway.Instance, CreateAIDecisionTransport(), inventoryRuntimeService, buyerPurchaseHandler);
            Debug.Log("AIActorSystem configured with " + aiActorSystem.Actors.Count + " actors.");
        }

        private IAIDecisionTransport CreateAIDecisionTransport()
        {
            string natsUrl = Environment.GetEnvironmentVariable("NATS_URL");
            if (string.IsNullOrWhiteSpace(natsUrl) && config != null)
            {
                natsUrl = config.NatsUrl;
            }

            return string.IsNullOrWhiteSpace(natsUrl)
                ? NullAIDecisionTransport.Instance
                : new CoreNatsAIDecisionTransport(natsUrl);
        }
        public bool TryApplyInventoryAdd(NetworkConnection connection, string commandId, string itemDefinitionId, int quantity, out InventoryMutationResult result)
        {
            result = null;
            if (!CanAcceptInventoryCommand(connection))
            {
                return false;
            }

            result = inventoryRuntimeService.AddItemCommand("player", InventoryOwnerId(connection), commandId, -1, itemDefinitionId, string.Empty, quantity);
            LogInventoryMutation("ADD", connection, result);
            return true;
        }

        public bool TryApplyInventoryCommand(NetworkConnection connection, InventoryCommand command, out InventoryMutationResult result)
        {
            result = null;
            if (!CanAcceptInventoryCommand(connection) || command == null)
            {
                return false;
            }

            result = inventoryRuntimeService.ApplyCommand("player", InventoryOwnerId(connection), command);
            LogInventoryMutation(command.Operation.ToString(), connection, result);
            return true;
        }


        public bool TryApplyBuyerPurchaseCommand(NetworkConnection connection, BuyerPurchaseCommand command, out SurvivalWorld.Economy.BuyerPurchaseResult result)
        {
            result = null;
            if (!CanAcceptBuyerPurchaseCommand(connection) || command == null)
            {
                return false;
            }

            Vector3 purchaserPosition = Vector3.zero;
            if (spawnedPlayers.TryGetValue(connection.ClientId, out NetworkObject player) && player != null)
            {
                purchaserPosition = player.transform.position;
            }

            var actor = new SurvivalWorld.Economy.BuyerPurchaseActor("player", InventoryOwnerId(connection), purchaserPosition, true);
            result = buyerPurchaseHandler.Handle(actor, command);
            string error = string.IsNullOrWhiteSpace(result.Error) ? string.Empty : ", error=" + result.Error;
            Debug.Log("Buyer purchase command result: connection=" + connection.ClientId + ", status=" + result.Status + ", api_status=" + result.ApiStatus + ", version=" + (result.Snapshot == null ? -1 : result.Snapshot.Version) + error);
            return true;
        }

        private bool CanAcceptBuyerPurchaseCommand(NetworkConnection connection)
        {
            if (connection == null || !connection.IsAuthenticated || !authenticatedClientIds.Contains(connection.ClientId))
            {
                Debug.LogWarning("Rejected buyer purchase command from unauthenticated connection.");
                return false;
            }

            if (buyerPurchaseHandler == null)
            {
                Debug.LogWarning("Rejected buyer purchase command because economy runtime is not ready.");
                return false;
            }

            return true;
        }
        public InventorySnapshot GetInventorySnapshot(NetworkConnection connection)
        {
            if (connection == null || inventoryRuntimeService == null)
            {
                return null;
            }

            return inventoryRuntimeService.RequestSnapshot("player", InventoryOwnerId(connection));
        }

        private bool CanAcceptInventoryCommand(NetworkConnection connection)
        {
            if (connection == null || !connection.IsAuthenticated || !authenticatedClientIds.Contains(connection.ClientId))
            {
                Debug.LogWarning("Rejected inventory command from unauthenticated connection.");
                return false;
            }

            if (inventoryRuntimeService == null)
            {
                Debug.LogWarning("Rejected inventory command because inventory runtime is not ready.");
                return false;
            }

            return true;
        }

        private static string InventoryOwnerId(NetworkConnection connection)
        {
            return "connection:" + connection.ClientId.ToString(CultureInfo.InvariantCulture);
        }

        private static void LogInventoryMutation(string operation, NetworkConnection connection, InventoryMutationResult result)
        {
            if (result == null)
            {
                Debug.LogWarning("Inventory command " + operation + " returned no result.");
                return;
            }

            string eventId = result.DomainEvent == null ? string.Empty : result.DomainEvent.EventId;
            string error = string.IsNullOrWhiteSpace(result.Error) ? string.Empty : ", error=" + result.Error;
            Debug.Log("Inventory command applied: op=" + operation + ", connection=" + connection.ClientId + ", status=" + result.Status + ", version=" + (result.Snapshot == null ? -1 : result.Snapshot.Version) + ", event_id=" + eventId + error);
        }


        private SurvivalWorld.Economy.IEconomyClient CreateEconomyClient()
        {
            if (config == null)
            {
                return SurvivalWorld.Economy.NullEconomyClient.Instance;
            }

            if (!EndpointParser.TryParse(config.EconomyGrpcEndpoint, out var endpoint))
            {
                Debug.LogWarning("Invalid Economy gRPC endpoint: " + config.EconomyGrpcEndpoint);
                return SurvivalWorld.Economy.NullEconomyClient.Instance;
            }

            economyGrpcChannel = CreateGrpcChannel(endpoint, "Economy");
            if (economyGrpcChannel == null)
            {
                return SurvivalWorld.Economy.NullEconomyClient.Instance;
            }

            var client = new EconomyService.EconomyServiceClient(economyGrpcChannel);
            return new SurvivalWorld.Economy.GeneratedEconomyGrpcClient(client);
        }
        private IWorldDataGateway CreateWorldDataGateway()
        {
            if (config == null)
            {
                return null;
            }

            if (!EndpointParser.TryParse(config.WorldDataGrpcEndpoint, out var endpoint))
            {
                Debug.LogWarning("Invalid WorldData gRPC endpoint: " + config.WorldDataGrpcEndpoint);
                return null;
            }

            worldDataGrpcChannel = CreateGrpcChannel(endpoint, "WorldData");
            if (worldDataGrpcChannel == null)
            {
                return null;
            }

            var client = new WorldDataService.WorldDataServiceClient(worldDataGrpcChannel);
            return new GeneratedWorldDataGateway(client, config.WorldDataGrpcSharedSecret);
        }

        private IActorStateGateway CreateActorStateGateway()
        {
            if (worldDataGrpcChannel == null)
            {
                return NullActorStateGateway.Instance;
            }

            var client = new ActorStateService.ActorStateServiceClient(worldDataGrpcChannel);
            string secret = config == null ? string.Empty : config.WorldDataGrpcSharedSecret;
            return new GeneratedActorStateGateway(client, secret);
        }

        private async UniTask LoadWorldSceneIfNeededAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(worldSceneName) || UnitySceneManager.GetActiveScene().name == worldSceneName)
            {
                return;
            }

            AsyncOperation operation = UnitySceneManager.LoadSceneAsync(worldSceneName);
            if (operation == null)
            {
                Debug.LogWarning("Failed to load server world scene: " + worldSceneName);
                return;
            }

            await operation.ToUniTask(cancellationToken: cancellationToken);
        }

        private void OnAuthenticationResult(NetworkConnection connection, bool authenticated)
        {
            if (connection == null)
            {
                return;
            }

            if (authenticated)
            {
                authenticatedClientIds.Add(connection.ClientId);
                TrySpawnPlayer(connection);
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
            if (scene.name != worldSceneName || networkManager == null)
            {
                return;
            }

            foreach (int clientId in authenticatedClientIds)
            {
                if (networkManager.ServerManager.Clients.TryGetValue(clientId, out NetworkConnection connection))
                {
                    TrySpawnPlayer(connection);
                }
            }
        }

        private void TrySpawnPlayer(NetworkConnection connection)
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
                Debug.LogWarning("ServerBootstrap requires a PlayerCharacter NetworkObject prefab for authenticated player spawn.");
                return;
            }

            Vector3 spawnPosition = playerPrefab.transform.position + new Vector3(spawnedPlayers.Count * 2f, 0f, 0f);
            NetworkObject player = networkManager.GetPooledInstantiated(playerPrefab, spawnPosition, playerPrefab.transform.rotation, true);
            if (player == null)
            {
                Debug.LogWarning("Failed to instantiate authenticated player prefab.");
                return;
            }

            networkManager.ServerManager.Spawn(player, connection, UnitySceneManager.GetActiveScene());
            networkManager.SceneManager.AddOwnerToDefaultScene(player);
            spawnedPlayers[connection.ClientId] = player;
            Debug.Log($"Spawned player for connection {connection.ClientId} at {spawnPosition}.");
        }

        private void OnDestroy()
        {
            if (!serverBootstrapActive)
            {
                return;
            }

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

            string serverId = config == null ? string.Empty : config.ServerId;
            DrainAndShutdownAsync(serverId, matchmakingGateway, persistenceAgent, aiActorSystem, authGrpcChannel, worldDataGrpcChannel, economyGrpcChannel).Forget();
            authGrpcChannel = null;
            worldDataGrpcChannel = null;
            economyGrpcChannel = null;
            persistenceAgent = null;
            aiActorSystem = null;
            inventoryRuntimeService = null;
        }

        private static async UniTaskVoid DrainAndShutdownAsync(string serverId, IMatchmakingGateway gateway, RuntimePersistenceAgent persistenceAgent, AIActorSystem aiActorSystem, ChannelBase authChannel, ChannelBase worldDataChannel, ChannelBase economyChannel)
        {
            if (persistenceAgent != null)
            {
                try
                {
                    await persistenceAgent.FlushAsync(CancellationToken.None);
                    await persistenceAgent.SaveSnapshotAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("World persistence drain failed: " + ex.Message);
                }
            }

            if (aiActorSystem != null)
            {
                try
                {
                    await aiActorSystem.SaveAllAsync(CancellationToken.None);
                    aiActorSystem.Stop();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("AI actor state drain failed: " + ex.Message);
                }
            }

            if (!string.IsNullOrWhiteSpace(serverId) && gateway != null)
            {
                try
                {
                    await gateway.MarkDrainingAsync(serverId, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("MarkDraining failed: " + ex.Message);
                }
            }

            await ShutdownChannelAsync("Auth", authChannel);
            await ShutdownChannelAsync("WorldData", worldDataChannel);
            await ShutdownChannelAsync("Economy", economyChannel);
        }

        private static async UniTask ShutdownChannelAsync(string label, ChannelBase channel)
        {
            if (channel == null)
            {
                return;
            }

            try
            {
                await channel.ShutdownAsync().AsUniTask();
            }
            catch (Exception ex)
            {
                Debug.LogWarning(label + " gRPC channel shutdown failed: " + ex.Message);
            }
        }
    }
}





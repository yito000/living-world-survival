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
using SurvivalWorld.Client.Interaction;
using SurvivalWorld.Config;
using SurvivalWorld.Dev;
using SurvivalWorld.Inventory;
using SurvivalWorld.Items;
using SurvivalWorld.Net;
using SurvivalWorld.Server.AI;
using SurvivalWorld.Server.Combat;
using SurvivalWorld.Server.Handlers;
using SurvivalWorld.Server.Inventory;
using SurvivalWorld.Server.Simulation;
using SurvivalWorld.Shared.Events;
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
        private InteractionCommandHandler interactionCommandHandler;
        private PrimaryActionCommandHandler primaryActionCommandHandler;
        private M3InventoryService m3InventoryService;
        private ResourceNodeSystem resourceNodeSystem;
        private StationJobSystem stationJobSystem;
        private FarmPlotSystem farmPlotSystem;
        private HuntingSystem huntingSystem;
        private CleaningSystem cleaningSystem;
        private readonly Dictionary<string, InventoryOwner> playtestInventoryOwners = new Dictionary<string, InventoryOwner>(StringComparer.Ordinal);
        private readonly Dictionary<string, ResourceNodeState> playtestResourceNodes = new Dictionary<string, ResourceNodeState>(StringComparer.Ordinal);
        private readonly Dictionary<string, StationState> playtestStations = new Dictionary<string, StationState>(StringComparer.Ordinal);
        private readonly Dictionary<string, FarmPlotState> playtestFarmPlots = new Dictionary<string, FarmPlotState>(StringComparer.Ordinal);
        private readonly Dictionary<uint, AnimalState> playtestAnimals = new Dictionary<uint, AnimalState>();
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

                PlaytestScenarioSeeder.EnsureSeededForCurrentScene();
                RegisterSceneInteractionTargets();

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
                    ConfigurePlaytestInteractionRuntime(NullInventoryEventSink.Instance);
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
                ConfigurePlaytestInteractionRuntime(persistenceAgent);
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

        private void ConfigurePlaytestInteractionRuntime(IInventoryEventSink eventSink)
        {
            MasterDataStore masterData = MasterDataStore.CreateM3Defaults();
            m3InventoryService = new M3InventoryService(M3ItemDefinitions.CreateCatalog());
            string worldId = config == null ? "world-m8a" : config.WorldId;
            var factory = new DomainEventFactory(worldId);
            IInventoryEventSink sink = eventSink ?? NullInventoryEventSink.Instance;
            resourceNodeSystem = new ResourceNodeSystem(masterData, m3InventoryService, factory, sink);
            stationJobSystem = new StationJobSystem(masterData, m3InventoryService, factory, sink);
            farmPlotSystem = new FarmPlotSystem(m3InventoryService, factory, sink);
            huntingSystem = new HuntingSystem(masterData, m3InventoryService, new DamageService(), factory, sink);
            cleaningSystem = new CleaningSystem(m3InventoryService, factory, sink);
            interactionCommandHandler = new InteractionCommandHandler();
            primaryActionCommandHandler = new PrimaryActionCommandHandler(huntingSystem);
            playtestInventoryOwners.Clear();
            playtestResourceNodes.Clear();
            playtestStations.Clear();
            playtestFarmPlots.Clear();
            playtestAnimals.Clear();
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
        private void RegisterSceneInteractionTargets()
        {
            if (interactionCommandHandler == null)
            {
                return;
            }

            PlaytestScenarioSeeder.EnsureSeededForCurrentScene();
            InteractableTargetView[] views = FindObjectsByType<InteractableTargetView>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < views.Length; i++)
            {
                RegisterInteractionTarget(views[i]);
            }
        }

        private void RegisterInteractionTarget(InteractableTargetView view)
        {
            if (view == null)
            {
                return;
            }

            uint id = view.ResolveTargetNetworkId();
            if (id == 0)
            {
                return;
            }

            var target = new InteractionTarget();
            switch (view.Kind)
            {
                case InteractionKind.Mine:
                    target.ResourceNodeSystem = resourceNodeSystem;
                    target.ResourceNode = GetOrCreateResourceNode(view);
                    break;
                case InteractionKind.StationCraft:
                case InteractionKind.StationCancel:
                    target.StationJobSystem = stationJobSystem;
                    target.Station = GetOrCreateStation(view);
                    break;
                case InteractionKind.FarmPlant:
                case InteractionKind.FarmHarvest:
                    target.FarmPlotSystem = farmPlotSystem;
                    target.FarmPlot = GetOrCreateFarmPlot(view);
                    break;
                case InteractionKind.Clean:
                    target.CleaningSystem = cleaningSystem;
                    target.WorldItemId = view.ServerStateId;
                    if (cleaningSystem != null)
                    {
                        cleaningSystem.RegisterWorldItem(new WorldItemState(view.ServerStateId, "food_waste", 1, view.transform.position, true));
                    }
                    break;
                case InteractionKind.Butcher:
                    RegisterCarcassTarget(view, string.Empty);
                    return;
                case InteractionKind.Animal:
                    GetOrCreateAnimal(id, view);
                    return;
                default:
                    return;
            }

            interactionCommandHandler.RegisterTarget(id, target);
        }

        private ResourceNodeState GetOrCreateResourceNode(InteractableTargetView view)
        {
            string key = view.ServerStateId;
            if (!playtestResourceNodes.TryGetValue(key, out ResourceNodeState node))
            {
                node = new ResourceNodeState(key, view.ResourceType, view.transform.position, view.MaximumAmount);
                playtestResourceNodes[key] = node;
            }
            node.Position = view.transform.position;
            return node;
        }

        private StationState GetOrCreateStation(InteractableTargetView view)
        {
            string key = view.ServerStateId;
            if (!playtestStations.TryGetValue(key, out StationState station))
            {
                station = new StationState(key, view.StationType);
                playtestStations[key] = station;
            }
            return station;
        }

        private FarmPlotState GetOrCreateFarmPlot(InteractableTargetView view)
        {
            string key = view.ServerStateId;
            if (!playtestFarmPlots.TryGetValue(key, out FarmPlotState plot))
            {
                plot = new FarmPlotState(key);
                playtestFarmPlots[key] = plot;
            }
            return plot;
        }

        private AnimalState GetOrCreateAnimal(uint id, InteractableTargetView view)
        {
            if (!playtestAnimals.TryGetValue(id, out AnimalState animal))
            {
                animal = new AnimalState(view.ServerStateId, "deer", 70f);
                playtestAnimals[id] = animal;
            }
            return animal;
        }

        private void RegisterCarcassTarget(InteractableTargetView view, string carcassId)
        {
            if (view == null || huntingSystem == null || interactionCommandHandler == null)
            {
                return;
            }

            string resolvedCarcassId = string.IsNullOrWhiteSpace(carcassId) ? view.ServerStateId : carcassId;
            if (!huntingSystem.Carcasses.ContainsKey(resolvedCarcassId))
            {
                return;
            }

            interactionCommandHandler.RegisterTarget(view.ResolveTargetNetworkId(), new InteractionTarget
            {
                HuntingSystem = huntingSystem,
                CarcassId = resolvedCarcassId
            });
        }

        private void RegisterCarcassTargets(string carcassId)
        {
            if (string.IsNullOrWhiteSpace(carcassId))
            {
                return;
            }

            InteractableTargetView[] views = FindObjectsByType<InteractableTargetView>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < views.Length; i++)
            {
                if (views[i] != null && views[i].Kind == InteractionKind.Butcher)
                {
                    RegisterCarcassTarget(views[i], carcassId);
                }
            }
        }
        public bool TryApplyInteractCommand(NetworkConnection connection, InteractCommand command, out M3CommandResult result)
        {
            result = M3CommandResult.Rejected("Rejected before interaction.");
            if (!CanAcceptPlaytestCommand(connection) || command == null || interactionCommandHandler == null)
            {
                return false;
            }

            RegisterSceneInteractionTargets();
            InteractionActorContext actor = CreateInteractionActorContext(connection);
            result = interactionCommandHandler.Handle(command, actor, UnixNowMs());
            string error = string.IsNullOrWhiteSpace(result.Error) ? string.Empty : ", error=" + result.Error;
            Debug.Log("Interact command applied: connection=" + connection.ClientId + ", type=" + command.InteractionType + ", success=" + result.Success + error);
            return true;
        }

        public bool TryApplyPrimaryActionCommand(NetworkConnection connection, PrimaryActionCommand command, out HuntingAttackResult result)
        {
            result = HuntingAttackResult.Rejected("Rejected before primary action.");
            if (!CanAcceptPlaytestCommand(connection) || command == null || primaryActionCommandHandler == null)
            {
                return false;
            }

            RegisterSceneInteractionTargets();
            if (!TryResolvePrimaryActionTarget(command, out AnimalState animal, out string error))
            {
                result = HuntingAttackResult.Rejected(error);
                return true;
            }

            result = primaryActionCommandHandler.Handle(command, InventoryOwnerId(connection), CombatantType.Player, animal, UnixNowMs());
            if (result.Killed)
            {
                RegisterCarcassTargets(result.CarcassId);
            }

            return true;
        }

        private bool CanAcceptPlaytestCommand(NetworkConnection connection)
        {
            if (connection == null || !connection.IsAuthenticated || !authenticatedClientIds.Contains(connection.ClientId))
            {
                Debug.LogWarning("Rejected playtest command from unauthenticated connection.");
                return false;
            }

            if (m3InventoryService == null || interactionCommandHandler == null)
            {
                Debug.LogWarning("Rejected playtest command because M3 playtest runtime is not ready.");
                return false;
            }

            return true;
        }
        private InteractionActorContext CreateInteractionActorContext(NetworkConnection connection)
        {
            InventoryOwner owner = GetOrCreatePlaytestInventoryOwner(connection);
            Vector3 position = Vector3.zero;
            if (spawnedPlayers.TryGetValue(connection.ClientId, out NetworkObject player) && player != null)
            {
                position = player.transform.position;
            }

            return new InteractionActorContext
            {
                ActorId = InventoryOwnerId(connection),
                Inventory = owner,
                Position = position,
                HasLineOfSight = true,
                ToolTags = ResolveToolTags(owner),
                ToolQuality = 1,
                DropSeed = Math.Max(1, connection.ClientId)
            };
        }

        private InventoryOwner GetOrCreatePlaytestInventoryOwner(NetworkConnection connection)
        {
            string ownerId = InventoryOwnerId(connection);
            if (!playtestInventoryOwners.TryGetValue(ownerId, out InventoryOwner owner))
            {
                owner = new InventoryOwner("player", ownerId, InventoryOwner.DefaultSlotCapacity, InventoryOwner.DefaultWeightCapacity, 0L);
                playtestInventoryOwners[ownerId] = owner;
                SeedPlaytestInventory(owner);
            }
            return owner;
        }

        private void SeedPlaytestInventory(InventoryOwner owner)
        {
            if (m3InventoryService == null || owner == null || owner.Entries.Count > 0)
            {
                return;
            }

            m3InventoryService.Grant(owner, new ItemStack("stone_pickaxe", 1));
            m3InventoryService.Grant(owner, new ItemStack("stone", 5));
            m3InventoryService.Grant(owner, new ItemStack("wood", 3));
            m3InventoryService.Grant(owner, new ItemStack("raw_meat", 1));
            m3InventoryService.Grant(owner, new ItemStack("food_waste", 1));
        }

        private string[] ResolveToolTags(InventoryOwner owner)
        {
            if (m3InventoryService != null && m3InventoryService.CountAvailable(owner, "stone_pickaxe") > 0)
            {
                return new[] { "tool.mining" };
            }

            return Array.Empty<string>();
        }

        private bool TryResolvePrimaryActionTarget(PrimaryActionCommand command, out AnimalState animal, out string error)
        {
            animal = null;
            error = string.Empty;
            Vector3 origin = ToVector3(command.AimOrigin);
            Vector3 direction = ToVector3(command.AimDirection);
            if (direction.sqrMagnitude <= 0.0001f)
            {
                error = "Aim direction is required.";
                return false;
            }

            if (!Physics.SphereCast(origin, 0.45f, direction.normalized, out RaycastHit hit, 8f, ~0, QueryTriggerInteraction.Collide))
            {
                error = "No primary action target.";
                return false;
            }

            InteractableTargetView view = hit.collider == null ? null : hit.collider.GetComponentInParent<InteractableTargetView>();
            if (view == null)
            {
                error = "Primary action target is not registered.";
                return false;
            }

            if (view.Kind != InteractionKind.Animal)
            {
                error = "Primary action target is not an animal.";
                return false;
            }

            uint id = view.ResolveTargetNetworkId();
            if (!playtestAnimals.TryGetValue(id, out animal))
            {
                animal = GetOrCreateAnimal(id, view);
            }

            return animal != null;
        }

        private static Vector3 ToVector3(Vec3 value)
        {
            return value == null ? Vector3.zero : new Vector3(value.X, value.Y, value.Z);
        }

        private static long UnixNowMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
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

            PlaytestScenarioSeeder.EnsureSeededForCurrentScene();
            RegisterSceneInteractionTargets();
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





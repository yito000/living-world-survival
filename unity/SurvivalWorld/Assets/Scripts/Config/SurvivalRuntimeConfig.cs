using UnityEngine;

namespace SurvivalWorld.Config
{
    [CreateAssetMenu(fileName = "SurvivalRuntimeConfig", menuName = "Survival World/Runtime Config")]
    public sealed class SurvivalRuntimeConfig : ScriptableObject
    {
        [Header("Client")]
        [SerializeField] private string authBaseUrl = "http://127.0.0.1:8081";
        [SerializeField] private string clientServerEndpointOverride = string.Empty;
        [SerializeField] private string buildId = "dev-local";

        [Header("Dedicated Server")]
        [SerializeField] private string serverId = "00000000-0000-0000-0000-000000000101";
        [SerializeField] private string worldId = "00000000-0000-0000-0000-000000000201";
        [SerializeField] private string serverEndpoint = "127.0.0.1:7770";
        [SerializeField] private ushort serverPort = 7770;
        [SerializeField] private int serverCapacity = 32;
        [SerializeField] private int tickMilliseconds = 50;
        [SerializeField] private float heartbeatSeconds = 5f;

        [Header("Auth gRPC")]
        [SerializeField] private string authGrpcEndpoint = "127.0.0.1:9091";
        [SerializeField] private string authGrpcSharedSecret = string.Empty;

        [Header("WorldData gRPC")]
        [SerializeField] private string worldDataGrpcEndpoint = "127.0.0.1:8092";
        [SerializeField] private string worldDataGrpcSharedSecret = string.Empty;
        [SerializeField] private int outboxFlushIntervalMilliseconds = 1000;
        [SerializeField] private int snapshotIntervalSeconds = 30;

        [Header("Economy gRPC")]
        [SerializeField] private string economyGrpcEndpoint = "127.0.0.1:9092";
        [SerializeField] private string economyGrpcSharedSecret = string.Empty;

        [Header("NATS")]
        [SerializeField] private string natsUrl = "nats://127.0.0.1:4222";

        [Header("Join Ticket")]
        [TextArea(3, 6)]
        [SerializeField] private string joinTicketPublicKey = string.Empty;

        [Header("Dev Local Mode")]
        [SerializeField] private bool devLocalMode;
        [SerializeField] private bool autoStartLocalServerInEditor = true;
        [SerializeField] private bool autoConnectLocalClientInEditor = true;
        [SerializeField] private string devAccountId = "dev-account-01";
        [SerializeField] private string devCharacterId = "00000000-0000-0000-0000-000000000001";

        public string AuthBaseUrl => authBaseUrl;
        public string ClientServerEndpointOverride => clientServerEndpointOverride;
        public string BuildId => buildId;
        public string ServerId => serverId;
        public string WorldId => worldId;
        public string ServerEndpoint => serverEndpoint;
        public ushort ServerPort => serverPort;
        public int ServerCapacity => serverCapacity;
        public int TickMilliseconds => tickMilliseconds;
        public float HeartbeatSeconds => Mathf.Max(1f, heartbeatSeconds);
        public string AuthGrpcEndpoint => authGrpcEndpoint;
        public string AuthGrpcSharedSecret => authGrpcSharedSecret;
        public string WorldDataGrpcEndpoint => worldDataGrpcEndpoint;
        public string WorldDataGrpcSharedSecret => worldDataGrpcSharedSecret;
        public int OutboxFlushIntervalMilliseconds => Mathf.Max(100, outboxFlushIntervalMilliseconds);
        public int SnapshotIntervalSeconds => Mathf.Max(1, snapshotIntervalSeconds);
        public string EconomyGrpcEndpoint => economyGrpcEndpoint;
        public string EconomyGrpcSharedSecret => economyGrpcSharedSecret;
        public string NatsUrl => natsUrl;
        public string JoinTicketPublicKey => joinTicketPublicKey;
        public bool DevLocalMode => devLocalMode;
        public bool AutoStartLocalServerInEditor => autoStartLocalServerInEditor;
        public bool AutoConnectLocalClientInEditor => autoConnectLocalClientInEditor;
        public string DevAccountId => devAccountId;
        public string DevCharacterId => devCharacterId;
    }
}


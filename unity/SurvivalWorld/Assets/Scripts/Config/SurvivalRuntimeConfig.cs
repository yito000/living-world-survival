using UnityEngine;

namespace SurvivalWorld.Config
{
    [CreateAssetMenu(fileName = "SurvivalRuntimeConfig", menuName = "Survival World/Runtime Config")]
    public sealed class SurvivalRuntimeConfig : ScriptableObject
    {
        [Header("Client")]
        [SerializeField] private string authBaseUrl = "http://127.0.0.1:8080";
        [SerializeField] private string buildId = "dev-local";

        [Header("Dedicated Server")]
        [SerializeField] private string serverId = "local-ds-01";
        [SerializeField] private string worldId = "world-mvp";
        [SerializeField] private string serverEndpoint = "127.0.0.1:7770";
        [SerializeField] private ushort serverPort = 7770;
        [SerializeField] private int serverCapacity = 32;
        [SerializeField] private int tickMilliseconds = 50;
        [SerializeField] private float heartbeatSeconds = 5f;

        [Header("Join Ticket")]
        [TextArea(3, 6)]
        [SerializeField] private string joinTicketPublicKey = string.Empty;

        [Header("Dev Local Mode")]
        [SerializeField] private bool devLocalMode;
        [SerializeField] private bool autoStartLocalServerInEditor = true;
        [SerializeField] private bool autoConnectLocalClientInEditor = true;
        [SerializeField] private string devAccountId = "dev-account-01";
        [SerializeField] private string devCharacterId = "dev-character-01";

        public string AuthBaseUrl => authBaseUrl;
        public string BuildId => buildId;
        public string ServerId => serverId;
        public string WorldId => worldId;
        public string ServerEndpoint => serverEndpoint;
        public ushort ServerPort => serverPort;
        public int ServerCapacity => serverCapacity;
        public int TickMilliseconds => tickMilliseconds;
        public float HeartbeatSeconds => Mathf.Max(1f, heartbeatSeconds);
        public string JoinTicketPublicKey => joinTicketPublicKey;
        public bool DevLocalMode => devLocalMode;
        public bool AutoStartLocalServerInEditor => autoStartLocalServerInEditor;
        public bool AutoConnectLocalClientInEditor => autoConnectLocalClientInEditor;
        public string DevAccountId => devAccountId;
        public string DevCharacterId => devCharacterId;
    }
}
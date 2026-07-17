using UnityEngine;

namespace SurvivalWorld.World
{
    /// <summary>
    /// Metadata attached to prefabs generated from the external asset manifest.
    /// </summary>
    public sealed class GeneratedAssetMetadata : MonoBehaviour
    {
        [SerializeField] private string assetId;
        [SerializeField] private string assetVersion;
        [SerializeField] private string kit;
        [SerializeField] private string assetName;
        [SerializeField] private int gridSize;
        [SerializeField] private bool serverPrefab;

        public string AssetId => assetId;
        public string AssetVersion => assetVersion;
        public string Kit => kit;
        public string AssetName => assetName;
        public int GridSize => gridSize;
        public bool ServerPrefab => serverPrefab;

        public void Configure(string id, string version, string kitName, string name, int grid, bool server)
        {
            assetId = id ?? string.Empty;
            assetVersion = version ?? string.Empty;
            kit = kitName ?? string.Empty;
            assetName = name ?? string.Empty;
            gridSize = Mathf.Max(1, grid);
            serverPrefab = server;
        }
    }

    /// <summary>
    /// Marker for deterministic module connection sockets generated from manifest entries.
    /// </summary>
    public sealed class GeneratedSocket : MonoBehaviour
    {
        [SerializeField] private string socketName;

        public string SocketName => socketName;

        public void Configure(string value)
        {
            socketName = value ?? string.Empty;
        }
    }

    /// <summary>
    /// Marker for client and server interaction points generated from manifest entries.
    /// </summary>
    public sealed class GeneratedInteractionPoint : MonoBehaviour
    {
        [SerializeField] private string interactionId;

        public string InteractionId => interactionId;

        public void Configure(string value)
        {
            interactionId = value ?? string.Empty;
        }
    }
}

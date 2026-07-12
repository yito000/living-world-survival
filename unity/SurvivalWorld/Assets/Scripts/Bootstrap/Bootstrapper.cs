using System.Threading;
using Cysharp.Threading.Tasks;
using SurvivalWorld.Auth;
using SurvivalWorld.Config;
using SurvivalWorld.Net;
using UnityEngine;

namespace SurvivalWorld.Bootstrap
{
    public sealed class Bootstrapper : MonoBehaviour
    {
        [SerializeField] private SurvivalRuntimeConfig config;
        [SerializeField] private NetworkSessionClient sessionClient;
        [SerializeField] private string defaultCharacterId = "dev-character";

        private AuthClient authClient;

        public AuthClient AuthClient => authClient;

        private void Awake()
        {
            if (config != null)
            {
                authClient = new AuthClient(config.AuthBaseUrl, new InMemoryTokenStore());
            }

            if (sessionClient == null)
            {
                sessionClient = FindFirstObjectByType<NetworkSessionClient>();
            }
        }

        public async UniTask LoginJoinAndConnectAsync(string email, string password, CancellationToken cancellationToken)
        {
            if (config == null || authClient == null || sessionClient == null)
            {
                Debug.LogWarning("Bootstrapper requires config, AuthClient, and NetworkSessionClient.");
                return;
            }

            await authClient.LoginAsync(email, password, cancellationToken);
            MatchmakingJoinResponse match = await authClient.JoinMatchmakingAsync(defaultCharacterId, config.BuildId, cancellationToken);
            await sessionClient.ConnectWithJoinTicketAsync(match.server_endpoint, match.join_ticket, cancellationToken);
        }
    }
}

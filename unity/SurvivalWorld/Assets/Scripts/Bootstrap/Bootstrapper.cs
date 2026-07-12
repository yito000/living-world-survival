using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using SurvivalWorld.Auth;
using SurvivalWorld.Config;
using SurvivalWorld.Dev;
using SurvivalWorld.Net;
using UnityEngine;

namespace SurvivalWorld.Bootstrap
{
    public sealed class Bootstrapper : MonoBehaviour
    {
        [SerializeField] private SurvivalRuntimeConfig config;
        [SerializeField] private NetworkSessionClient sessionClient;
        [SerializeField] private string defaultCharacterId = "dev-character";

        private IAuthClient authClient;

        public IAuthClient AuthClient => authClient;

        private void Awake()
        {
            if (config != null)
            {
                authClient = CreateAuthClient();
            }

            if (sessionClient == null)
            {
                sessionClient = FindFirstObjectByType<NetworkSessionClient>();
            }
        }

        private void Start()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            StartDevLocalFlowAsync(destroyCancellationToken).Forget();
#endif
        }

        public async UniTask LoginJoinAndConnectAsync(string email, string password, CancellationToken cancellationToken)
        {
            if (config == null || authClient == null || sessionClient == null)
            {
                Debug.LogWarning("Bootstrapper requires config, AuthClient, and NetworkSessionClient.");
                return;
            }

            await authClient.LoginAsync(email, password, cancellationToken);
            string characterId = IsDevLocalModeActive() ? config.DevCharacterId : defaultCharacterId;
            MatchmakingJoinResponse match = await authClient.JoinMatchmakingAsync(characterId, config.BuildId, cancellationToken);
            await sessionClient.ConnectWithJoinTicketAsync(match.server_endpoint, match.join_ticket, cancellationToken);
        }

        private IAuthClient CreateAuthClient()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (config.DevLocalMode)
            {
                return new DevLocalAuthClient(config, new DevLocalJoinTicketIssuer(), new InMemoryTokenStore());
            }
#endif
            return new AuthClient(config.AuthBaseUrl, new InMemoryTokenStore());
        }

        private bool IsDevLocalModeActive()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            return config != null && config.DevLocalMode;
#else
            return false;
#endif
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private async UniTaskVoid StartDevLocalFlowAsync(CancellationToken cancellationToken)
        {
            await UniTask.Yield();
            cancellationToken.ThrowIfCancellationRequested();

            if (!Application.isEditor || !IsDevLocalModeActive() || !config.AutoConnectLocalClientInEditor)
            {
                return;
            }

            try
            {
                await LoginJoinAndConnectAsync("dev@example.local", "dev-password", cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }
#endif
    }
}
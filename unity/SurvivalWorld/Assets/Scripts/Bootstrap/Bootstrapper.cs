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
        private const string EditorEmail = "dev@example.local";
        private const string EditorPassword = "dev-password";
        private const string EditorDisplayName = "Dev Player";
        private const string AutoConnectArg = "--sw-auto-connect";
        private const string EmailArg = "--sw-email";
        private const string PasswordArg = "--sw-password";
        private const string DisplayNameArg = "--sw-display-name";
        private const string CharacterIdArg = "--sw-character-id";

        [SerializeField] private SurvivalRuntimeConfig config;
        [SerializeField] private NetworkSessionClient sessionClient;
        [SerializeField] private string defaultCharacterId = "00000000-0000-0000-0000-000000000301";

        private IAuthClient authClient;
        private string runtimeCharacterIdOverride;

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
            if (ShouldStartAutomaticConnectFlow())
            {
                StartAutomaticConnectFlowAsync(destroyCancellationToken).Forget();
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
            string characterId = !string.IsNullOrWhiteSpace(runtimeCharacterIdOverride)
                ? runtimeCharacterIdOverride
                : IsDevLocalModeActive() ? config.DevCharacterId : defaultCharacterId;
            MatchmakingJoinResponse match = await authClient.JoinMatchmakingAsync(characterId, config.BuildId, cancellationToken);
            string clientEndpoint = ResolveClientEndpoint(match.server_endpoint);
            Debug.Log($"Matchmaking joined: serverEndpoint={match.server_endpoint}, clientEndpoint={clientEndpoint}");
            await sessionClient.ConnectWithJoinTicketAsync(clientEndpoint, match.join_ticket, cancellationToken);
        }

        private async UniTask CreateAccountLoginJoinAndConnectAsync(string email, string password, string displayName, CancellationToken cancellationToken)
        {
            if (authClient == null)
            {
                Debug.LogWarning("Bootstrapper requires AuthClient.");
                return;
            }

            try
            {
                await authClient.CreateAccountAsync(email, password, displayName, cancellationToken);
            }
            catch (AuthClientException ex) when (ex.StatusCode == 409)
            {
                // Local backend smoke flow may leave the dev account in place between runs.
            }

            await LoginJoinAndConnectAsync(email, password, cancellationToken);
        }

        private string ResolveClientEndpoint(string matchmakingEndpoint)
        {
            if (config != null && !string.IsNullOrWhiteSpace(config.ClientServerEndpointOverride))
            {
                return config.ClientServerEndpointOverride;
            }

            return matchmakingEndpoint;
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

        private bool ShouldStartAutomaticConnectFlow()
        {
            if (Application.isBatchMode || config == null || !config.AutoConnectLocalClientInEditor)
            {
                return false;
            }

            return Application.isEditor || HasCommandLineArg(AutoConnectArg);
        }

        private async UniTaskVoid StartAutomaticConnectFlowAsync(CancellationToken cancellationToken)
        {
            await UniTask.Yield();
            cancellationToken.ThrowIfCancellationRequested();

            if (!ShouldStartAutomaticConnectFlow())
            {
                return;
            }

            try
            {
                string email = GetCommandLineValue(EmailArg, EditorEmail);
                string password = GetCommandLineValue(PasswordArg, EditorPassword);
                string displayName = GetCommandLineValue(DisplayNameArg, EditorDisplayName);
                runtimeCharacterIdOverride = GetCommandLineValue(CharacterIdArg, string.Empty);

                if (IsDevLocalModeActive())
                {
                    await LoginJoinAndConnectAsync(email, password, cancellationToken);
                    return;
                }

                await CreateAccountLoginJoinAndConnectAsync(email, password, displayName, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        private static bool HasCommandLineArg(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], name, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static string GetCommandLineValue(string name, string fallback)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.Ordinal))
                {
                    return args[i + 1];
                }
            }

            return fallback;
        }
    }
}
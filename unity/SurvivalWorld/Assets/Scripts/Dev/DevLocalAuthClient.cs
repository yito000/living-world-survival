using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using SurvivalWorld.Auth;
using SurvivalWorld.Config;

namespace SurvivalWorld.Dev
{
    public sealed class DevLocalAuthClient : IAuthClient
    {
        private static readonly TimeSpan JoinTicketTtl = TimeSpan.FromSeconds(30);

        private readonly SurvivalRuntimeConfig config;
        private readonly DevLocalJoinTicketIssuer issuer;
        private readonly ITokenStore tokenStore;

        public DevLocalAuthClient(SurvivalRuntimeConfig config, DevLocalJoinTicketIssuer issuer, ITokenStore tokenStore = null)
        {
            this.config = config != null ? config : throw new ArgumentNullException(nameof(config));
            this.issuer = issuer ?? throw new ArgumentNullException(nameof(issuer));
            this.tokenStore = tokenStore ?? new InMemoryTokenStore();
        }

        public ITokenStore TokenStore => tokenStore;

        public UniTask<CreateAccountResponse> CreateAccountAsync(string email, string password, string displayName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return UniTask.FromResult(new CreateAccountResponse
            {
                account_id = config.DevAccountId
            });
        }

        public UniTask<SessionTokenPair> LoginAsync(string email, string password, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tokens = new SessionTokenPair("dev-access-" + config.DevAccountId, "dev-refresh-" + config.DevAccountId, 3600);
            tokenStore.Set(tokens);
            return UniTask.FromResult(tokens);
        }

        public UniTask<SessionTokenPair> RefreshAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tokens = new SessionTokenPair("dev-access-" + config.DevAccountId, "dev-refresh-" + config.DevAccountId, 3600);
            tokenStore.Set(tokens);
            return UniTask.FromResult(tokens);
        }

        public UniTask LogoutAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            tokenStore.Clear();
            return UniTask.CompletedTask;
        }

        public UniTask<MatchmakingJoinResponse> JoinMatchmakingAsync(string characterId, string buildId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string resolvedCharacterId = string.IsNullOrWhiteSpace(characterId) ? config.DevCharacterId : characterId;
            string resolvedBuildId = string.IsNullOrWhiteSpace(buildId) ? config.BuildId : buildId;
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string ticket = issuer.Issue(config.DevAccountId, resolvedCharacterId, config.ServerId, config.WorldId, resolvedBuildId, JoinTicketTtl, nowMs);

            return UniTask.FromResult(new MatchmakingJoinResponse
            {
                server_endpoint = config.ServerEndpoint,
                join_ticket = ticket,
                expires_at = nowMs + (long)JoinTicketTtl.TotalMilliseconds
            });
        }
    }
}
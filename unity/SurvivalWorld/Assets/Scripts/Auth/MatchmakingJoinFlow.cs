using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace SurvivalWorld.Auth
{
    /// <summary>
    /// Handles authenticated matchmaking joins with a single session refresh retry.
    /// </summary>
    public sealed class MatchmakingJoinFlow
    {
        private const long UnauthorizedStatusCode = 401;

        private readonly IAuthClient authClient;

        public MatchmakingJoinFlow(IAuthClient authClient)
        {
            this.authClient = authClient ?? throw new ArgumentNullException(nameof(authClient));
        }

        public async UniTask<MatchmakingJoinResponse> JoinWithRefreshAsync(string characterId, string buildId, CancellationToken cancellationToken)
        {
            try
            {
                return await authClient.JoinMatchmakingAsync(characterId, buildId, cancellationToken);
            }
            catch (AuthClientException ex) when (ex.StatusCode == UnauthorizedStatusCode)
            {
                await authClient.RefreshAsync(cancellationToken);
                return await authClient.JoinMatchmakingAsync(characterId, buildId, cancellationToken);
            }
        }
    }
}

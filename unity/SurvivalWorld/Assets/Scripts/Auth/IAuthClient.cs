using System.Threading;
using Cysharp.Threading.Tasks;

namespace SurvivalWorld.Auth
{
    public interface IAuthClient
    {
        ITokenStore TokenStore { get; }
        UniTask<CreateAccountResponse> CreateAccountAsync(string email, string password, string displayName, CancellationToken cancellationToken);
        UniTask<SessionTokenPair> LoginAsync(string email, string password, CancellationToken cancellationToken);
        UniTask<SessionTokenPair> RefreshAsync(CancellationToken cancellationToken);
        UniTask LogoutAsync(CancellationToken cancellationToken);
        UniTask<MatchmakingJoinResponse> JoinMatchmakingAsync(string characterId, string buildId, CancellationToken cancellationToken);
    }
}
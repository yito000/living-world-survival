using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using SurvivalWorld.Auth;

namespace SurvivalWorld.Tests
{
    public sealed class M7SessionRecoveryTests
    {
        [Test]
        public async Task JoinWithRefreshRetriesMatchmakingAfterUnauthorized()
        {
            var authClient = new RefreshingAuthClient();
            var flow = new MatchmakingJoinFlow(authClient);

            MatchmakingJoinResponse response = await flow.JoinWithRefreshAsync("character-1", "build-1", CancellationToken.None);

            Assert.AreEqual("ticket-2", response.join_ticket);
            Assert.AreEqual(2, authClient.JoinAttempts);
            Assert.AreEqual(1, authClient.RefreshAttempts);
        }

        private sealed class RefreshingAuthClient : IAuthClient
        {
            public ITokenStore TokenStore { get; } = new InMemoryTokenStore();
            public int JoinAttempts { get; private set; }
            public int RefreshAttempts { get; private set; }

            public UniTask<CreateAccountResponse> CreateAccountAsync(string email, string password, string displayName, CancellationToken cancellationToken)
            {
                throw new System.NotSupportedException();
            }

            public UniTask<SessionTokenPair> LoginAsync(string email, string password, CancellationToken cancellationToken)
            {
                throw new System.NotSupportedException();
            }

            public UniTask<SessionTokenPair> RefreshAsync(CancellationToken cancellationToken)
            {
                RefreshAttempts++;
                return UniTask.FromResult(new SessionTokenPair("access-2", "refresh-2", 3600));
            }

            public UniTask LogoutAsync(CancellationToken cancellationToken)
            {
                throw new System.NotSupportedException();
            }

            public UniTask<MatchmakingJoinResponse> JoinMatchmakingAsync(string characterId, string buildId, CancellationToken cancellationToken)
            {
                JoinAttempts++;
                if (JoinAttempts == 1)
                {
                    throw new AuthClientException(401, "expired");
                }

                return UniTask.FromResult(new MatchmakingJoinResponse
                {
                    server_endpoint = "127.0.0.1:7770",
                    join_ticket = "ticket-2"
                });
            }
        }
    }
}

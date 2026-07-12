using System;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace SurvivalWorld.Auth
{
    public sealed class AuthClient : IAuthClient
    {
        private readonly string baseUrl;
        private readonly ITokenStore tokenStore;

        public AuthClient(string baseUrl, ITokenStore tokenStore = null)
        {
            this.baseUrl = NormalizeBaseUrl(baseUrl);
            this.tokenStore = tokenStore ?? new InMemoryTokenStore();
        }

        public ITokenStore TokenStore => tokenStore;

        public UniTask<CreateAccountResponse> CreateAccountAsync(string email, string password, string displayName, CancellationToken cancellationToken)
        {
            var request = new CreateAccountRequest
            {
                email = email,
                password = password,
                display_name = displayName
            };
            return SendJsonAsync<CreateAccountRequest, CreateAccountResponse>("POST", "/v1/accounts", request, null, cancellationToken);
        }

        public async UniTask<SessionTokenPair> LoginAsync(string email, string password, CancellationToken cancellationToken)
        {
            var response = await SendJsonAsync<LoginRequest, SessionResponse>("POST", "/v1/sessions", new LoginRequest
            {
                email = email,
                password = password
            }, null, cancellationToken);

            var tokens = new SessionTokenPair(response.access_token, response.refresh_token, response.expires_in);
            tokenStore.Set(tokens);
            return tokens;
        }

        public async UniTask<SessionTokenPair> RefreshAsync(CancellationToken cancellationToken)
        {
            if (tokenStore.Current == null || string.IsNullOrEmpty(tokenStore.Current.RefreshToken))
            {
                throw new AuthClientException(0, "No refresh token is available.");
            }

            var response = await SendJsonAsync<RefreshSessionRequest, SessionResponse>("POST", "/v1/sessions/refresh", new RefreshSessionRequest
            {
                refresh_token = tokenStore.Current.RefreshToken
            }, null, cancellationToken);

            var tokens = new SessionTokenPair(response.access_token, response.refresh_token, response.expires_in);
            tokenStore.Set(tokens);
            return tokens;
        }

        public async UniTask LogoutAsync(CancellationToken cancellationToken)
        {
            string accessToken = tokenStore.Current?.AccessToken;
            using (var request = UnityWebRequest.Delete(baseUrl + "/v1/sessions/current"))
            {
                AddBearer(request, accessToken);
                await SendAsync(request, cancellationToken);
            }

            tokenStore.Clear();
        }

        public UniTask<MatchmakingJoinResponse> JoinMatchmakingAsync(string characterId, string buildId, CancellationToken cancellationToken)
        {
            string accessToken = tokenStore.Current?.AccessToken;
            var request = new MatchmakingJoinRequest
            {
                character_id = characterId,
                build_id = buildId
            };
            return SendJsonAsync<MatchmakingJoinRequest, MatchmakingJoinResponse>("POST", "/v1/matchmaking/join", request, accessToken, cancellationToken);
        }

        private async UniTask<TResponse> SendJsonAsync<TRequest, TResponse>(string method, string path, TRequest body, string bearerToken, CancellationToken cancellationToken)
        {
            string json = JsonUtility.ToJson(body);
            byte[] payload = Encoding.UTF8.GetBytes(json);

            using (var request = new UnityWebRequest(baseUrl + path, method))
            {
                request.uploadHandler = new UploadHandlerRaw(payload);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Accept", "application/json");
                AddBearer(request, bearerToken);

                string responseBody = await SendAsync(request, cancellationToken);
                return JsonUtility.FromJson<TResponse>(responseBody);
            }
        }

        private static async UniTask<string> SendAsync(UnityWebRequest request, CancellationToken cancellationToken)
        {
            await request.SendWebRequest().ToUniTask(cancellationToken: cancellationToken);

            bool failed = request.result == UnityWebRequest.Result.ConnectionError ||
                          request.result == UnityWebRequest.Result.ProtocolError ||
                          request.result == UnityWebRequest.Result.DataProcessingError;
            string body = request.downloadHandler == null ? string.Empty : request.downloadHandler.text;
            if (failed)
            {
                throw new AuthClientException(request.responseCode, body);
            }

            return body;
        }

        private static void AddBearer(UnityWebRequest request, string token)
        {
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.SetRequestHeader("Authorization", "Bearer " + token);
            }
        }

        private static string NormalizeBaseUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "http://127.0.0.1:8080";
            }

            return value.TrimEnd('/');
        }
    }

    public sealed class AuthClientException : Exception
    {
        public AuthClientException(long statusCode, string responseBody)
            : base($"Auth request failed with HTTP {statusCode}: {responseBody}")
        {
            StatusCode = statusCode;
            ResponseBody = responseBody;
        }

        public long StatusCode { get; }
        public string ResponseBody { get; }
    }
}
using System;

namespace SurvivalWorld.Auth
{
    [Serializable]
    public sealed class CreateAccountRequest
    {
        public string email;
        public string password;
        public string display_name;
    }

    [Serializable]
    public sealed class CreateAccountResponse
    {
        public string account_id;
    }

    [Serializable]
    public sealed class LoginRequest
    {
        public string email;
        public string password;
    }

    [Serializable]
    public sealed class SessionResponse
    {
        public string access_token;
        public string refresh_token;
        public int expires_in;
    }

    [Serializable]
    public sealed class RefreshSessionRequest
    {
        public string refresh_token;
    }

    [Serializable]
    public sealed class MatchmakingJoinRequest
    {
        public string character_id;
        public string build_id;
    }

    [Serializable]
    public sealed class MatchmakingJoinResponse
    {
        public string server_endpoint;
        public string join_ticket;
        public long expires_at;
    }

    public sealed class SessionTokenPair
    {
        public SessionTokenPair(string accessToken, string refreshToken, int expiresInSeconds)
        {
            AccessToken = accessToken ?? string.Empty;
            RefreshToken = refreshToken ?? string.Empty;
            ExpiresInSeconds = expiresInSeconds;
        }

        public string AccessToken { get; }
        public string RefreshToken { get; }
        public int ExpiresInSeconds { get; }
    }
}

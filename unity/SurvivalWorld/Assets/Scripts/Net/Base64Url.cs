using System;
using System.Text;
using UnityEngine;

namespace SurvivalWorld.Net
{
    internal static class Base64Url
    {
        public static byte[] Decode(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return Array.Empty<byte>();
            }

            string padded = value.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2:
                    padded += "==";
                    break;
                case 3:
                    padded += "=";
                    break;
            }

            return Convert.FromBase64String(padded);
        }

        public static string DecodeUtf8(string value)
        {
            return Encoding.UTF8.GetString(Decode(value));
        }
    }

    [Serializable]
    internal sealed class JwsHeaderJson
    {
        public string alg;
        public string typ;
    }

    [Serializable]
    internal sealed class JoinTicketClaimsJson
    {
        public string ticket_id;
        public string account_id;
        public string character_id;
        public string server_id;
        public string world_id;
        public string build_id;
        public long issued_at_unix_ms;
        public long expires_at_unix_ms;
        public string nonce;
    }
}

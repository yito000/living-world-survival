using System;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using SurvivalWorld.Net;
using UnityEngine;

namespace SurvivalWorld.Dev
{
    public sealed class DevLocalJoinTicketIssuer
    {
        private static readonly byte[] DefaultPrivateKeySeed =
        {
            0x53, 0x57, 0x44, 0x45, 0x56, 0x4c, 0x4f, 0x43,
            0x41, 0x4c, 0x2d, 0x4a, 0x4f, 0x49, 0x4e, 0x2d,
            0x54, 0x49, 0x43, 0x4b, 0x45, 0x54, 0x2d, 0x30,
            0x30, 0x30, 0x31, 0x2d, 0x4b, 0x45, 0x59, 0x21
        };

        private readonly Ed25519PrivateKeyParameters privateKey;
        private readonly string publicKey;

        public DevLocalJoinTicketIssuer()
            : this(DefaultPrivateKeySeed)
        {
        }

        public DevLocalJoinTicketIssuer(byte[] privateKeySeed)
        {
            if (privateKeySeed == null || privateKeySeed.Length != 32)
            {
                throw new ArgumentException("Dev Ed25519 private key seed must be 32 bytes.", nameof(privateKeySeed));
            }

            privateKey = new Ed25519PrivateKeyParameters(privateKeySeed, 0);
            publicKey = Base64Url.Encode(privateKey.GeneratePublicKey().GetEncoded());
        }

        public string PublicKey => publicKey;

        public string Issue(string accountId, string characterId, string serverId, string worldId, string buildId, TimeSpan ttl, long? nowUnixMilliseconds = null)
        {
            long now = nowUnixMilliseconds ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long expiresAt = now + (long)ttl.TotalMilliseconds;
            var payload = new JoinTicketClaimsJson
            {
                ticket_id = "dev-ticket-" + Guid.NewGuid().ToString("N"),
                account_id = accountId ?? string.Empty,
                character_id = characterId ?? string.Empty,
                server_id = serverId ?? string.Empty,
                world_id = worldId ?? string.Empty,
                build_id = buildId ?? string.Empty,
                issued_at_unix_ms = now,
                expires_at_unix_ms = expiresAt,
                nonce = Guid.NewGuid().ToString("N")
            };

            string header = Base64Url.EncodeUtf8(JsonUtility.ToJson(new JwsHeaderJson { alg = "EdDSA", typ = "JWT" }));
            string claims = Base64Url.EncodeUtf8(JsonUtility.ToJson(payload));
            string signingInput = header + "." + claims;
            string signature = Base64Url.Encode(Sign(Encoding.ASCII.GetBytes(signingInput)));
            return signingInput + "." + signature;
        }

        private byte[] Sign(byte[] signingInput)
        {
            var signer = new Ed25519Signer();
            signer.Init(true, privateKey);
            signer.BlockUpdate(signingInput, 0, signingInput.Length);
            return signer.GenerateSignature();
        }
    }
}
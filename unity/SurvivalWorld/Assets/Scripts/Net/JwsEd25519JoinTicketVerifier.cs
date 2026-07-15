using System;
using System.Linq;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Survival.V1;
using UnityEngine;

namespace SurvivalWorld.Net
{
    public sealed class JwsEd25519JoinTicketVerifier : IJoinTicketVerifier
    {
        public JoinTicketValidationResult Verify(string compactJws, JoinTicketVerificationContext context)
        {
            if (string.IsNullOrWhiteSpace(compactJws))
            {
                return JoinTicketValidationResult.Reject(JoinTicketRejectionReason.EmptyTicket, "Join ticket is empty.");
            }

            string[] parts = compactJws.Split('.');
            if (parts.Length != 3)
            {
                return JoinTicketValidationResult.Reject(JoinTicketRejectionReason.MalformedTicket, "Join ticket must be compact JWS with three parts.");
            }

            JwsHeaderJson header;
            JoinTicketClaims claims;
            byte[] signature;
            try
            {
                header = JsonUtility.FromJson<JwsHeaderJson>(Base64Url.DecodeUtf8(parts[0]));
                claims = ToClaims(JsonUtility.FromJson<JoinTicketClaimsJson>(Base64Url.DecodeUtf8(parts[1])));
                signature = Base64Url.Decode(parts[2]);
            }
            catch (Exception ex) when (ex is FormatException || ex is ArgumentException)
            {
                return JoinTicketValidationResult.Reject(JoinTicketRejectionReason.MalformedTicket, ex.Message);
            }

            if (header == null || !string.Equals(header.alg, "EdDSA", StringComparison.Ordinal))
            {
                return JoinTicketValidationResult.Reject(JoinTicketRejectionReason.SignatureInvalid, "Join ticket JWS alg must be EdDSA.");
            }

            if (!VerifySignature(parts[0] + "." + parts[1], signature, context.PublicKey))
            {
                return JoinTicketValidationResult.Reject(JoinTicketRejectionReason.SignatureInvalid, "Join ticket signature is invalid.");
            }

            return JoinTicketClaimsValidator.ValidateLocalClaims(claims, context);
        }

        private static bool VerifySignature(string signingInput, byte[] signature, string publicKeyMaterial)
        {
            if (signature == null || signature.Length != 64 || string.IsNullOrWhiteSpace(publicKeyMaterial))
            {
                return false;
            }

            byte[] publicKey = DecodePublicKey(publicKeyMaterial);
            if (publicKey.Length != 32)
            {
                return false;
            }

            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
            byte[] input = Encoding.ASCII.GetBytes(signingInput);
            verifier.BlockUpdate(input, 0, input.Length);
            return verifier.VerifySignature(signature);
        }

        private static byte[] DecodePublicKey(string publicKeyMaterial)
        {
            string trimmed = publicKeyMaterial.Trim();
            if (trimmed.Contains("-----BEGIN", StringComparison.Ordinal))
            {
                string base64 = string.Join(string.Empty, trimmed.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(line => !line.StartsWith("-----", StringComparison.Ordinal)));
                byte[] der = Convert.FromBase64String(base64);
                return der.Length >= 32 ? der.Skip(der.Length - 32).ToArray() : der;
            }

            try
            {
                return Base64Url.Decode(trimmed);
            }
            catch (FormatException)
            {
                return Convert.FromBase64String(trimmed);
            }
        }

        private static JoinTicketClaims ToClaims(JoinTicketClaimsJson json)
        {
            if (json == null)
            {
                return null;
            }

            return new JoinTicketClaims
            {
                TicketId = FirstNonEmpty(json.ticket_id, json.jti),
                AccountId = FirstNonEmpty(json.account_id, json.sub),
                CharacterId = FirstNonEmpty(json.character_id, json.chr),
                ServerId = FirstNonEmpty(json.server_id, json.srv),
                WorldId = FirstNonEmpty(json.world_id, json.wld),
                BuildId = FirstNonEmpty(json.build_id, json.bld),
                IssuedAtUnixMs = FirstPositive(json.issued_at_unix_ms, json.iat_ms, SecondsToMilliseconds(json.iat)),
                ExpiresAtUnixMs = FirstPositive(json.expires_at_unix_ms, json.exp_ms, SecondsToMilliseconds(json.exp)),
                Nonce = json.nonce ?? string.Empty
            };
        }

        private static string FirstNonEmpty(string primary, string fallback)
        {
            return !string.IsNullOrWhiteSpace(primary) ? primary : fallback ?? string.Empty;
        }

        private static long FirstPositive(params long[] values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] > 0)
                {
                    return values[i];
                }
            }

            return 0;
        }

        private static long SecondsToMilliseconds(long seconds)
        {
            return seconds > 0 ? seconds * 1000L : 0;
        }
    }
}

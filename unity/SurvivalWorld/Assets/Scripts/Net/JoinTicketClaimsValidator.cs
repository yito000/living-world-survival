using System;
using Survival.V1;

namespace SurvivalWorld.Net
{
    public enum JoinTicketRejectionReason
    {
        None = 0,
        EmptyTicket,
        MalformedTicket,
        SignatureInvalid,
        Expired,
        ServerMismatch,
        BuildMismatch,
        InvalidCharacter,
        RedeemRejected,
        GatewayUnavailable
    }

    public readonly struct JoinTicketValidationResult
    {
        private JoinTicketValidationResult(bool accepted, JoinTicketRejectionReason reason, JoinTicketClaims claims, string message)
        {
            Accepted = accepted;
            Reason = reason;
            Claims = claims;
            Message = message ?? string.Empty;
        }

        public bool Accepted { get; }
        public JoinTicketRejectionReason Reason { get; }
        public JoinTicketClaims Claims { get; }
        public string Message { get; }

        public static JoinTicketValidationResult Accept(JoinTicketClaims claims)
        {
            return new JoinTicketValidationResult(true, JoinTicketRejectionReason.None, claims, string.Empty);
        }

        public static JoinTicketValidationResult Reject(JoinTicketRejectionReason reason, string message)
        {
            return new JoinTicketValidationResult(false, reason, null, message);
        }
    }

    public readonly struct JoinTicketVerificationContext
    {
        public JoinTicketVerificationContext(string publicKey, string expectedServerId, string expectedBuildId, Func<string, bool> characterAllowed, long nowUnixMilliseconds)
        {
            PublicKey = publicKey;
            ExpectedServerId = expectedServerId;
            ExpectedBuildId = expectedBuildId;
            CharacterAllowed = characterAllowed;
            NowUnixMilliseconds = nowUnixMilliseconds;
        }

        public string PublicKey { get; }
        public string ExpectedServerId { get; }
        public string ExpectedBuildId { get; }
        public Func<string, bool> CharacterAllowed { get; }
        public long NowUnixMilliseconds { get; }
    }

    public interface IJoinTicketVerifier
    {
        JoinTicketValidationResult Verify(string compactJws, JoinTicketVerificationContext context);
    }

    public static class JoinTicketClaimsValidator
    {
        public static JoinTicketValidationResult ValidateLocalClaims(JoinTicketClaims claims, JoinTicketVerificationContext context)
        {
            if (claims == null || string.IsNullOrWhiteSpace(claims.TicketId))
            {
                return JoinTicketValidationResult.Reject(JoinTicketRejectionReason.MalformedTicket, "Join ticket claims are missing.");
            }

            if (claims.ExpiresAtUnixMs <= context.NowUnixMilliseconds)
            {
                return JoinTicketValidationResult.Reject(JoinTicketRejectionReason.Expired, "Join ticket has expired.");
            }

            if (!string.Equals(claims.ServerId, context.ExpectedServerId, StringComparison.Ordinal))
            {
                return JoinTicketValidationResult.Reject(JoinTicketRejectionReason.ServerMismatch, "Join ticket server_id does not match this dedicated server.");
            }

            if (!string.Equals(claims.BuildId, context.ExpectedBuildId, StringComparison.Ordinal))
            {
                return JoinTicketValidationResult.Reject(JoinTicketRejectionReason.BuildMismatch, "Join ticket build_id does not match this build.");
            }

            bool characterAllowed = !string.IsNullOrWhiteSpace(claims.CharacterId) && (context.CharacterAllowed == null || context.CharacterAllowed(claims.CharacterId));
            if (!characterAllowed)
            {
                return JoinTicketValidationResult.Reject(JoinTicketRejectionReason.InvalidCharacter, "Join ticket character_id is not allowed.");
            }

            return JoinTicketValidationResult.Accept(claims);
        }
    }
}

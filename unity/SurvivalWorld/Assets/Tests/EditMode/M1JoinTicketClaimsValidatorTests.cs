using NUnit.Framework;
using Survival.V1;
using SurvivalWorld.Net;

namespace SurvivalWorld.Tests
{
    public sealed class M1JoinTicketClaimsValidatorTests
    {
        private const long NowMs = 1000;

        [Test]
        public void ValidateLocalClaimsAcceptsMatchingClaims()
        {
            JoinTicketValidationResult result = JoinTicketClaimsValidator.ValidateLocalClaims(ValidClaims(), Context());

            Assert.IsTrue(result.Accepted);
            Assert.AreEqual(JoinTicketRejectionReason.None, result.Reason);
            Assert.AreEqual("ticket-1", result.Claims.TicketId);
        }

        [Test]
        public void ValidateLocalClaimsRejectsExpiredTicket()
        {
            JoinTicketClaims claims = ValidClaims();
            claims.ExpiresAtUnixMs = NowMs;

            JoinTicketValidationResult result = JoinTicketClaimsValidator.ValidateLocalClaims(claims, Context());

            Assert.IsFalse(result.Accepted);
            Assert.AreEqual(JoinTicketRejectionReason.Expired, result.Reason);
        }

        [Test]
        public void ValidateLocalClaimsRejectsServerMismatch()
        {
            JoinTicketClaims claims = ValidClaims();
            claims.ServerId = "other-server";

            JoinTicketValidationResult result = JoinTicketClaimsValidator.ValidateLocalClaims(claims, Context());

            Assert.IsFalse(result.Accepted);
            Assert.AreEqual(JoinTicketRejectionReason.ServerMismatch, result.Reason);
        }

        [Test]
        public void ValidateLocalClaimsRejectsBuildMismatch()
        {
            JoinTicketClaims claims = ValidClaims();
            claims.BuildId = "other-build";

            JoinTicketValidationResult result = JoinTicketClaimsValidator.ValidateLocalClaims(claims, Context());

            Assert.IsFalse(result.Accepted);
            Assert.AreEqual(JoinTicketRejectionReason.BuildMismatch, result.Reason);
        }

        [Test]
        public void ValidateLocalClaimsRejectsInvalidCharacter()
        {
            JoinTicketClaims claims = ValidClaims();
            claims.CharacterId = "blocked-character";

            JoinTicketValidationResult result = JoinTicketClaimsValidator.ValidateLocalClaims(claims, Context());

            Assert.IsFalse(result.Accepted);
            Assert.AreEqual(JoinTicketRejectionReason.InvalidCharacter, result.Reason);
        }

        private static JoinTicketVerificationContext Context()
        {
            return new JoinTicketVerificationContext("unused", "server-1", "build-1", characterId => characterId == "character-1", NowMs);
        }

        private static JoinTicketClaims ValidClaims()
        {
            return new JoinTicketClaims
            {
                TicketId = "ticket-1",
                AccountId = "account-1",
                CharacterId = "character-1",
                ServerId = "server-1",
                WorldId = "world-1",
                BuildId = "build-1",
                IssuedAtUnixMs = 900,
                ExpiresAtUnixMs = 2000,
                Nonce = "nonce-1"
            };
        }
    }
}
using System;
using NUnit.Framework;
using SurvivalWorld.Dev;
using SurvivalWorld.Net;

namespace SurvivalWorld.Tests
{
    public sealed class M1aDevLocalTicketTests
    {
        [Test]
        public void IssueCreatesTicketAcceptedByExistingVerifier()
        {
            var issuer = new DevLocalJoinTicketIssuer();
            string ticket = issuer.Issue("account-1", "character-1", "server-1", "world-1", "build-1", TimeSpan.FromSeconds(30), 1000);

            JoinTicketValidationResult result = Verify(ticket, issuer.PublicKey, "server-1", "build-1", 1001);

            Assert.IsTrue(result.Accepted);
            Assert.AreEqual("account-1", result.Claims.AccountId);
            Assert.AreEqual("character-1", result.Claims.CharacterId);
        }

        [Test]
        public void VerifyRejectsExpiredDevTicket()
        {
            var issuer = new DevLocalJoinTicketIssuer();
            string ticket = issuer.Issue("account-1", "character-1", "server-1", "world-1", "build-1", TimeSpan.Zero, 1000);

            JoinTicketValidationResult result = Verify(ticket, issuer.PublicKey, "server-1", "build-1", 1000);

            Assert.IsFalse(result.Accepted);
            Assert.AreEqual(JoinTicketRejectionReason.Expired, result.Reason);
        }

        [Test]
        public void VerifyRejectsServerMismatch()
        {
            var issuer = new DevLocalJoinTicketIssuer();
            string ticket = issuer.Issue("account-1", "character-1", "server-1", "world-1", "build-1", TimeSpan.FromSeconds(30), 1000);

            JoinTicketValidationResult result = Verify(ticket, issuer.PublicKey, "other-server", "build-1", 1001);

            Assert.IsFalse(result.Accepted);
            Assert.AreEqual(JoinTicketRejectionReason.ServerMismatch, result.Reason);
        }

        [Test]
        public void VerifyRejectsBuildMismatch()
        {
            var issuer = new DevLocalJoinTicketIssuer();
            string ticket = issuer.Issue("account-1", "character-1", "server-1", "world-1", "build-1", TimeSpan.FromSeconds(30), 1000);

            JoinTicketValidationResult result = Verify(ticket, issuer.PublicKey, "server-1", "other-build", 1001);

            Assert.IsFalse(result.Accepted);
            Assert.AreEqual(JoinTicketRejectionReason.BuildMismatch, result.Reason);
        }

        private static JoinTicketValidationResult Verify(string ticket, string publicKey, string serverId, string buildId, long nowMs)
        {
            var verifier = new JwsEd25519JoinTicketVerifier();
            var context = new JoinTicketVerificationContext(publicKey, serverId, buildId, characterId => !string.IsNullOrWhiteSpace(characterId), nowMs);
            return verifier.Verify(ticket, context);
        }
    }
}
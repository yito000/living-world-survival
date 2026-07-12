using System;
using System.Threading;
using NUnit.Framework;
using SurvivalWorld.Dev;
using SurvivalWorld.Server;

namespace SurvivalWorld.Tests
{
    public sealed class M1aDevLocalGatewayTests
    {
        [Test]
        public void RedeemJoinTicketRejectsSecondUse()
        {
            var issuer = new DevLocalJoinTicketIssuer();
            var gateway = new DevLocalMatchmakingGateway("build-1", issuer.PublicKey);
            string ticket = issuer.Issue("account-1", "character-1", "server-1", "world-1", "build-1", TimeSpan.FromSeconds(30));

            MatchmakingGatewayResult first = gateway.RedeemJoinTicketAsync("server-1", ticket, CancellationToken.None).GetAwaiter().GetResult();
            MatchmakingGatewayResult second = gateway.RedeemJoinTicketAsync("server-1", ticket, CancellationToken.None).GetAwaiter().GetResult();

            Assert.IsTrue(first.Ok);
            Assert.IsFalse(second.Ok);
            Assert.AreEqual("join_ticket_reused", second.Error);
        }

        [Test]
        public void RedeemJoinTicketRejectsBuildMismatch()
        {
            var issuer = new DevLocalJoinTicketIssuer();
            var gateway = new DevLocalMatchmakingGateway("other-build", issuer.PublicKey);
            string ticket = issuer.Issue("account-1", "character-1", "server-1", "world-1", "build-1", TimeSpan.FromSeconds(30));

            MatchmakingGatewayResult result = gateway.RedeemJoinTicketAsync("server-1", ticket, CancellationToken.None).GetAwaiter().GetResult();

            Assert.IsFalse(result.Ok);
            Assert.AreEqual("join_ticket_build_mismatch", result.Error);
        }
    }
}
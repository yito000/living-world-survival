using System.Collections;
using System.Threading;
using NUnit.Framework;
using SurvivalWorld.Auth;
using SurvivalWorld.Config;
using SurvivalWorld.Dev;
using UnityEngine;
using UnityEngine.TestTools;

namespace SurvivalWorld.Tests
{
    public sealed class M1aDevLocalBootstrapPlayModeTests
    {
        [UnityTest]
        public IEnumerator DevLocalAuthJoinReturnsLocalEndpointAndTicket()
        {
            SurvivalRuntimeConfig config = ScriptableObject.CreateInstance<SurvivalRuntimeConfig>();
            var authClient = new DevLocalAuthClient(config, new DevLocalJoinTicketIssuer());

            MatchmakingJoinResponse response = authClient.JoinMatchmakingAsync(config.DevCharacterId, config.BuildId, CancellationToken.None).GetAwaiter().GetResult();

            Assert.AreEqual(config.ServerEndpoint, response.server_endpoint);
            Assert.IsNotEmpty(response.join_ticket);
            Assert.Greater(response.expires_at, 0);

            Object.Destroy(config);
            yield return null;
        }
    }
}
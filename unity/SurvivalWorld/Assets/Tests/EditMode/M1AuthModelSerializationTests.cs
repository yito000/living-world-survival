using NUnit.Framework;
using SurvivalWorld.Auth;
using UnityEngine;

namespace SurvivalWorld.Tests
{
    public sealed class M1AuthModelSerializationTests
    {
        [Test]
        public void MatchmakingJoinRequestSerializesExpectedJsonFields()
        {
            string json = JsonUtility.ToJson(new MatchmakingJoinRequest
            {
                character_id = "character-1",
                build_id = "build-1"
            });

            StringAssert.Contains("\"character_id\":\"character-1\"", json);
            StringAssert.Contains("\"build_id\":\"build-1\"", json);
        }

        [Test]
        public void SessionResponseDeserializesTokenPairShape()
        {
            SessionResponse response = JsonUtility.FromJson<SessionResponse>("{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_in\":3600}");

            Assert.AreEqual("access", response.access_token);
            Assert.AreEqual("refresh", response.refresh_token);
            Assert.AreEqual(3600, response.expires_in);
        }
    }
}
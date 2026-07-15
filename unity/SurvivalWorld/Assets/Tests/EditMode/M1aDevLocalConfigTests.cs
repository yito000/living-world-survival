using NUnit.Framework;
using SurvivalWorld.Auth;
using SurvivalWorld.Config;
using UnityEngine;

namespace SurvivalWorld.Tests
{
    public sealed class M1aDevLocalConfigTests
    {
        [Test]
        public void RuntimeConfigDisablesDevLocalModeByDefault()
        {
            SurvivalRuntimeConfig config = ScriptableObject.CreateInstance<SurvivalRuntimeConfig>();

            Assert.IsFalse(config.DevLocalMode);
            Assert.IsTrue(config.AutoStartLocalServerInEditor);
            Assert.IsTrue(config.AutoConnectLocalClientInEditor);

            Object.DestroyImmediate(config);
        }

        [Test]
        public void RuntimeConfigDefaultsTargetLocalBackend()
        {
            SurvivalRuntimeConfig config = ScriptableObject.CreateInstance<SurvivalRuntimeConfig>();

            Assert.AreEqual("http://127.0.0.1:8081", config.AuthBaseUrl);
            Assert.AreEqual(string.Empty, config.ClientServerEndpointOverride);
            Assert.AreEqual("127.0.0.1:9091", config.AuthGrpcEndpoint);
            Assert.AreEqual("127.0.0.1:7770", config.ServerEndpoint);
            Assert.IsTrue(System.Guid.TryParse(config.ServerId, out _));
            Assert.IsTrue(System.Guid.TryParse(config.WorldId, out _));
            Assert.IsTrue(System.Guid.TryParse(config.DevCharacterId, out _));

            Object.DestroyImmediate(config);
        }

        [Test]
        public void AuthClientImplementsAuthInterface()
        {
            Assert.IsInstanceOf<IAuthClient>(new AuthClient("http://127.0.0.1:8081"));
        }
    }
}
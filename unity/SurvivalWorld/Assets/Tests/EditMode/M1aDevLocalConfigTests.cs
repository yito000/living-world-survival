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
        public void AuthClientImplementsAuthInterface()
        {
            Assert.IsInstanceOf<IAuthClient>(new AuthClient("http://127.0.0.1:8080"));
        }
    }
}
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace SurvivalWorld.Tests
{
    public sealed class M0PlayModeSmokeTests
    {
        [UnityTest]
        public IEnumerator ProjectEntersPlayMode()
        {
            yield return null;
            Assert.That(Application.isPlaying, Is.True);
        }
    }
}

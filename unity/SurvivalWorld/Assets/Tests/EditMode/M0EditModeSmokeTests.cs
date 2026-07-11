using NUnit.Framework;
using UnityEngine;

namespace SurvivalWorld.Tests
{
    public sealed class M0EditModeSmokeTests
    {
        [Test]
        public void ProjectHasConfiguredProductName()
        {
            Assert.That(Application.productName, Is.EqualTo("SurvivalWorld"));
        }
    }
}

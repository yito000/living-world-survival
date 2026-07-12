using NUnit.Framework;
using SurvivalWorld.Net;

namespace SurvivalWorld.Tests
{
    public sealed class M1EndpointParserTests
    {
        [Test]
        public void TryParseAcceptsHostPort()
        {
            Assert.IsTrue(EndpointParser.TryParse("127.0.0.1:7770", out ServerEndpoint endpoint));
            Assert.AreEqual("127.0.0.1", endpoint.Host);
            Assert.AreEqual(7770, endpoint.Port);
        }

        [Test]
        public void TryParseAcceptsAbsoluteUri()
        {
            Assert.IsTrue(EndpointParser.TryParse("fishnet://localhost:7771", out ServerEndpoint endpoint));
            Assert.AreEqual("localhost", endpoint.Host);
            Assert.AreEqual(7771, endpoint.Port);
        }

        [TestCase("")]
        [TestCase("localhost")]
        [TestCase(":7770")]
        [TestCase("localhost:not-a-port")]
        [TestCase("localhost:70000")]
        public void TryParseRejectsInvalidEndpoint(string value)
        {
            Assert.IsFalse(EndpointParser.TryParse(value, out _));
        }
    }
}
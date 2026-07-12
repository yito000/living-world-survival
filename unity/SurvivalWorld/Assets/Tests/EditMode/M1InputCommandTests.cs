using NUnit.Framework;
using Survival.V1;
using SurvivalWorld.Player;
using UnityEngine;

namespace SurvivalWorld.Tests
{
    public sealed class M1InputCommandTests
    {
        [Test]
        public void BuildCommandAssignsTickAndMonotonicSequence()
        {
            GameObject gameObject = new GameObject("input-reader-test");
            try
            {
                ThirdPersonInputReader reader = gameObject.AddComponent<ThirdPersonInputReader>();

                InputCommand first = reader.BuildCommand(Vector2.up, new Vector2(2f, -1f), true, false, 90f, 42);
                InputCommand second = reader.BuildCommand(Vector2.zero, Vector2.zero, false, true, 90f, 43);

                Assert.AreEqual(42, first.Tick);
                Assert.AreEqual(1, first.Sequence);
                Assert.AreEqual(2, first.Look.X);
                Assert.AreEqual(-1, first.Look.Y);
                Assert.IsTrue(first.Jump);
                Assert.IsFalse(first.Sprint);

                Assert.AreEqual(43, second.Tick);
                Assert.AreEqual(2, second.Sequence);
                Assert.IsTrue(second.Sprint);
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void BuildCommandUsesCameraRelativeClampedMove()
        {
            GameObject gameObject = new GameObject("input-reader-test");
            try
            {
                ThirdPersonInputReader reader = gameObject.AddComponent<ThirdPersonInputReader>();

                InputCommand forwardWithYaw = reader.BuildCommand(Vector2.up, Vector2.zero, false, false, 90f, 1);
                InputCommand diagonal = reader.BuildCommand(new Vector2(10f, 10f), Vector2.zero, false, false, 0f, 2);
                Vector3 diagonalMove = new Vector3(diagonal.Move.X, diagonal.Move.Y, diagonal.Move.Z);

                Assert.AreEqual(1f, forwardWithYaw.Move.X, 0.0001f);
                Assert.AreEqual(0f, forwardWithYaw.Move.Y, 0.0001f);
                Assert.AreEqual(0f, forwardWithYaw.Move.Z, 0.0001f);
                Assert.LessOrEqual(diagonalMove.magnitude, 1.0001f);
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }
    }
}
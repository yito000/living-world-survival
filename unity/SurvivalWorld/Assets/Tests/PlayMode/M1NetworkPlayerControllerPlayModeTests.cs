using System.Collections;
using System.Reflection;
using NUnit.Framework;
using Survival.V1;
using SurvivalWorld.Player;
using UnityEngine;
using UnityEngine.TestTools;

namespace SurvivalWorld.Tests
{
    public sealed class M1NetworkPlayerControllerPlayModeTests
    {
        [UnityTest]
        public IEnumerator ApplyMovementMovesCharacterForward()
        {
            GameObject gameObject = new GameObject("network-player-controller-test");
            gameObject.AddComponent<CharacterController>();
            NetworkPlayerController controller = gameObject.AddComponent<NetworkPlayerController>();
            yield return null;

            MethodInfo method = typeof(NetworkPlayerController).GetMethod("ApplyMovement", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method);

            Vector3 start = gameObject.transform.position;
            var command = new InputCommand
            {
                Tick = 1,
                Sequence = 1,
                Move = new Vec3 { X = 0f, Y = 0f, Z = 1f },
                Look = new Vec3(),
                Jump = false,
                Sprint = false
            };

            method.Invoke(controller, new object[] { command, 0.2f });
            yield return null;

            Assert.Greater(gameObject.transform.position.z, start.z + 0.01f);
            Object.Destroy(gameObject);
        }
    }
}
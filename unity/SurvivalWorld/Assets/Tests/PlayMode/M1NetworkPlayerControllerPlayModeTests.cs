using System.Collections;
using System.Reflection;
using NUnit.Framework;
using StarterAssets;
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

        [UnityTest]
        public IEnumerator ThirdPersonInputReaderConsumesStarterAssetsInputs()
        {
            GameObject gameObject = new GameObject("starter-assets-input-reader-test");
            StarterAssetsInputs inputs = gameObject.AddComponent<StarterAssetsInputs>();
            ThirdPersonInputReader reader = gameObject.AddComponent<ThirdPersonInputReader>();
            yield return null;

            inputs.MoveInput(new Vector2(0f, 1f));
            inputs.LookInput(new Vector2(2f, -1f));
            inputs.JumpInput(true);
            inputs.SprintInput(true);

            InputCommand command = reader.ReadCurrentCommand(1, 90f);
            Assert.Greater(command.Move.X, 0.9f);
            Assert.AreEqual(0f, command.Move.Z, 0.001f);
            Assert.AreEqual(2f, command.Look.X, 0.001f);
            Assert.AreEqual(-1f, command.Look.Y, 0.001f);
            Assert.IsTrue(command.Jump);
            Assert.IsTrue(command.Sprint);

            InputCommand heldCommand = reader.ReadCurrentCommand(2, 90f);
            Assert.IsFalse(heldCommand.Jump);

            Object.Destroy(gameObject);
        }
    }
}

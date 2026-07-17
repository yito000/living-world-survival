using NUnit.Framework;
using Survival.V1;
using SurvivalWorld.Client.Interaction;
using SurvivalWorld.Inventory;
using SurvivalWorld.Server;
using SurvivalWorld.Server.Handlers;
using SurvivalWorld.Server.Simulation;
using UnityEngine;

namespace SurvivalWorld.Tests
{
    public sealed class M8APlaytestInteractionsEditModeTests
    {
        [Test]
        public void InteractionScannerBuildsCandidateFromTargetView()
        {
            GameObject cameraObject = new GameObject("scanner-camera");
            GameObject targetObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            GameObject scannerObject = new GameObject("scanner");
            try
            {
                Camera camera = cameraObject.AddComponent<Camera>();
                cameraObject.transform.position = new Vector3(0f, 0f, -5f);
                cameraObject.transform.rotation = Quaternion.identity;
                targetObject.transform.position = Vector3.zero;
                InteractableTargetView target = targetObject.AddComponent<InteractableTargetView>();
                target.Configure(InteractionKind.Mine, 42, "Stone", "E Mine Stone", "mine", 7, "stone-node", "stone", string.Empty, string.Empty, 10, false);

                InteractionScanner scanner = scannerObject.AddComponent<InteractionScanner>();
                scanner.SourceCamera = camera;
                scanner.Range = 10f;
                Physics.SyncTransforms();

                Assert.IsTrue(scanner.Scan());
                Assert.IsTrue(scanner.TryGetCandidate(out InteractionCandidate candidate));
                Assert.AreEqual(42u, candidate.TargetNetworkId);
                Assert.AreEqual("mine", candidate.InteractionType);
                Assert.AreEqual(7, candidate.ExpectedVersion);
                Assert.AreEqual("E Mine Stone", candidate.PromptText);
            }
            finally
            {
                Object.DestroyImmediate(scannerObject);
                Object.DestroyImmediate(targetObject);
                Object.DestroyImmediate(cameraObject);
            }
        }

        [Test]
        public void InteractionCommandHandlerRejectsUnsupportedRegisteredType()
        {
            var handler = new InteractionCommandHandler();
            handler.RegisterTarget(7, new InteractionTarget());
            var actor = new InteractionActorContext
            {
                ActorId = "actor-1",
                Inventory = new InventoryOwner("player", "actor-1"),
                Position = Vector3.zero
            };

            M3CommandResult result = handler.Handle(new InteractCommand { TargetNetworkId = 7, InteractionType = "dance" }, actor, 1L);

            Assert.IsFalse(result.Success);
            StringAssert.Contains("Unsupported interaction type", result.Error);
        }

        [Test]
        public void ServerBootstrapRejectsUnauthenticatedInteractCommand()
        {
            GameObject gameObject = new GameObject("server-bootstrap-test");
            try
            {
                ServerBootstrap bootstrap = gameObject.AddComponent<ServerBootstrap>();
                bool accepted = bootstrap.TryApplyInteractCommand(null, new InteractCommand { TargetNetworkId = 1, InteractionType = "mine" }, out M3CommandResult result);

                Assert.IsFalse(accepted);
                Assert.IsFalse(result.Success);
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }
    }
}
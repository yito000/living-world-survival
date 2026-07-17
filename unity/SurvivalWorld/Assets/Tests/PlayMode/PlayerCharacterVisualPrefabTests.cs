#if UNITY_EDITOR
using FishNet.Object;
using NUnit.Framework;
using StarterAssets;
using SurvivalWorld.Player;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SurvivalWorld.Tests
{
    public sealed class PlayerCharacterVisualPrefabTests
    {
        [Test]
        public void PlayerCharacterPrefabKeepsNetworkRootAndUsesStarterAssetsInputAndAnimator()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/PlayerCharacter.prefab");

            Assert.IsNotNull(prefab);
            Assert.IsNotNull(prefab.GetComponent<NetworkObject>());
            Assert.IsNotNull(prefab.GetComponent<CharacterController>());
            Assert.IsNotNull(prefab.GetComponent<NetworkPlayerController>());
            Assert.IsNotNull(prefab.GetComponent<NetworkInventoryCommandBridge>());
            Assert.IsNotNull(prefab.GetComponent<ThirdPersonInputReader>());
            Assert.IsNotNull(prefab.GetComponent<StarterAssetsInputs>());

            PlayerInput playerInput = prefab.GetComponent<PlayerInput>();
            Assert.IsNotNull(playerInput);
            Assert.IsNotNull(playerInput.actions);
            Assert.AreEqual("StarterAssets", playerInput.actions.name);
            Assert.AreEqual("Player", playerInput.defaultActionMap);
            Assert.AreEqual(PlayerNotifications.SendMessages, playerInput.notificationBehavior);

            Transform visualRoot = prefab.transform.Find("VisualRoot");
            Assert.IsNotNull(visualRoot);
            Assert.AreEqual(new Vector3(0f, -0.88f, 0f), visualRoot.localPosition);

            Transform armature = visualRoot.Find("PlayerArmature");
            Assert.IsNotNull(armature);
            Assert.IsNull(armature.GetComponent<ThirdPersonController>());
            Assert.IsNull(armature.GetComponent<PlayerInput>());
            Assert.IsNull(armature.GetComponent<CharacterController>());
            Assert.IsTrue(visualRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length > 0);

            Animator animator = visualRoot.GetComponentInChildren<Animator>(true);
            Assert.IsNotNull(animator);
            Assert.IsNotNull(animator.runtimeAnimatorController);
            Assert.AreEqual("StarterAssetsThirdPerson", animator.runtimeAnimatorController.name);
            AssertHasAnimatorParameter(animator, "Speed", AnimatorControllerParameterType.Float);
            AssertHasAnimatorParameter(animator, "MotionSpeed", AnimatorControllerParameterType.Float);
            AssertHasAnimatorParameter(animator, "Grounded", AnimatorControllerParameterType.Bool);
            AssertHasAnimatorParameter(animator, "Jump", AnimatorControllerParameterType.Bool);
            AssertHasAnimatorParameter(animator, "FreeFall", AnimatorControllerParameterType.Bool);

            SerializedObject controller = new SerializedObject(prefab.GetComponent<NetworkPlayerController>());
            Assert.IsNotNull(controller.FindProperty("inputReader").objectReferenceValue);
            Assert.IsNotNull(controller.FindProperty("starterAssetsInputs").objectReferenceValue);
            Assert.IsNotNull(controller.FindProperty("animator").objectReferenceValue);
        }

        private static void AssertHasAnimatorParameter(Animator animator, string parameterName, AnimatorControllerParameterType type)
        {
            foreach (AnimatorControllerParameter parameter in animator.parameters)
            {
                if (parameter.name == parameterName && parameter.type == type)
                {
                    return;
                }
            }

            Assert.Fail($"Animator parameter was not found: {parameterName} ({type}).");
        }
    }
}
#endif

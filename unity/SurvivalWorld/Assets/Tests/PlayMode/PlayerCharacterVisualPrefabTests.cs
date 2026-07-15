#if UNITY_EDITOR
using FishNet.Object;
using NUnit.Framework;
using SurvivalWorld.Player;
using UnityEditor;
using UnityEngine;

namespace SurvivalWorld.Tests
{
    public sealed class PlayerCharacterVisualPrefabTests
    {
        [Test]
        public void PlayerCharacterPrefabKeepsNetworkRootAndHasVisualModel()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/PlayerCharacter.prefab");

            Assert.IsNotNull(prefab);
            Assert.IsNotNull(prefab.GetComponent<NetworkObject>());
            Assert.IsNotNull(prefab.GetComponent<CharacterController>());
            Assert.IsNotNull(prefab.GetComponent<NetworkPlayerController>());
            Assert.IsNotNull(prefab.GetComponent<NetworkInventoryCommandBridge>());

            Transform visualRoot = prefab.transform.Find("VisualRoot");
            Assert.IsNotNull(visualRoot);
            Assert.AreEqual(new Vector3(0f, -0.88f, 0f), visualRoot.localPosition);
            Assert.IsNotNull(visualRoot.Find("passive_marker_man"));
            Assert.IsTrue(visualRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length > 0);
        }
    }
}
#endif

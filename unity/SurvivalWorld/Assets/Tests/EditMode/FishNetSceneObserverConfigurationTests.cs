#if UNITY_EDITOR
using System.Linq;
using FishNet.Managing;
using FishNet.Managing.Observing;
using FishNet.Observing;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace SurvivalWorld.Tests
{
    public sealed class FishNetSceneObserverConfigurationTests
    {
        private const string BootstrapScenePath = "Assets/Scenes/Bootstrap.unity";
        private const string SceneConditionPath = "Packages/com.firstgeargames.fishnet/Runtime/Observing/Conditions/ScriptableObjects/SceneCondition.asset";

        [Test]
        public void BootstrapNetworkManagerUsesSceneConditionForSceneObjects()
        {
            Scene existingScene = SceneManager.GetSceneByPath(BootstrapScenePath);
            bool openedForTest = !existingScene.IsValid() || !existingScene.isLoaded;
            Scene scene = openedForTest
                ? EditorSceneManager.OpenScene(BootstrapScenePath, OpenSceneMode.Additive)
                : existingScene;

            try
            {
                NetworkManager networkManager = scene.GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<NetworkManager>(true))
                    .SingleOrDefault();
                Assert.IsNotNull(networkManager, "Bootstrap scene must contain one FishNet NetworkManager.");

                ObserverManager observerManager = networkManager.GetComponent<ObserverManager>();
                Assert.IsNotNull(observerManager, "Scene NetworkObjects require ObserverManager default conditions.");

                ObserverCondition sceneCondition = AssetDatabase.LoadAssetAtPath<ObserverCondition>(SceneConditionPath);
                Assert.IsNotNull(sceneCondition, "FishNet SceneCondition asset must be available.");

                SerializedObject serialized = new SerializedObject(observerManager);
                SerializedProperty defaultConditions = serialized.FindProperty("_defaultConditions");
                Assert.IsNotNull(defaultConditions);

                bool containsSceneCondition = Enumerable.Range(0, defaultConditions.arraySize)
                    .Select(index => defaultConditions.GetArrayElementAtIndex(index).objectReferenceValue)
                    .Any(reference => reference == sceneCondition);

                Assert.IsTrue(containsSceneCondition, "ObserverManager default conditions must include SceneCondition.");
            }
            finally
            {
                if (openedForTest)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
        }
    }
}
#endif

using System.Collections;
using NUnit.Framework;
using SurvivalWorld.Client.Interaction;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace SurvivalWorld.Tests
{
    public sealed class M8APlaytestArenaPlayModeTests
    {
        [UnityTest]
        public IEnumerator WorldMvpSeedsPlaytestArenaTargets()
        {
            SceneManager.LoadScene("World_MVP", LoadSceneMode.Single);
            yield return null;
            PlaytestScenarioSeeder.EnsureSeededForCurrentScene();
            yield return null;

            GameObject arena = GameObject.Find("PlaytestArena");
            Assert.IsNotNull(arena);
            Assert.GreaterOrEqual(Object.FindObjectsByType<InteractableTargetView>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length, 8);
        }
    }
}
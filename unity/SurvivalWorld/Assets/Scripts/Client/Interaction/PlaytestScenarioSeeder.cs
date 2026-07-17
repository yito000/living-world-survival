using UnityEngine;
using UnityEngine.SceneManagement;

namespace SurvivalWorld.Client.Interaction
{
    public sealed class PlaytestScenarioSeeder : MonoBehaviour
    {
        private const string WorldSceneName = "World_MVP";
        private const string ArenaName = "PlaytestArena";
        private static bool sceneHookRegistered;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void RegisterSceneHook()
        {
            if (sceneHookRegistered) return;
            sceneHookRegistered = true;
            SceneManager.sceneLoaded += (_, _) => EnsureSeededForCurrentScene();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void SeedInitialScene() => EnsureSeededForCurrentScene();

        public static GameObject EnsureSeededForCurrentScene()
        {
            if (!ShouldSeed()) return null;
            GameObject existing = GameObject.Find(ArenaName);
            if (existing != null) return existing;
            var root = new GameObject(ArenaName);
            root.AddComponent<PlaytestScenarioSeeder>();
            AddTargets(root.transform);
            return root;
        }

        private static bool ShouldSeed()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            return SceneManager.GetActiveScene().name == WorldSceneName;
#else
            return false;
#endif
        }

        private static void AddTargets(Transform root)
        {
            T(root, PrimitiveType.Cube, "Stone Resource Node", 1001, InteractionKind.Mine, "Mine Stone", "mine", "stone-node-1", "stone", "", "", 10, false, new Vector3(0f, .5f, 6f), new Vector3(1.4f, 1f, 1.4f), new Color(.45f, .47f, .5f));
            T(root, PrimitiveType.Cube, "Iron Resource Node", 1002, InteractionKind.Mine, "Mine Iron", "mine", "iron-node-1", "iron", "", "", 8, false, new Vector3(2.5f, .5f, 6f), new Vector3(1.2f, 1f, 1.2f), new Color(.55f, .42f, .35f));
            T(root, PrimitiveType.Cube, "Workbench Anvil", 1003, InteractionKind.StationCraft, "Craft Stone Spear", "", "anvil-1", "", "anvil", "stone_spear", 1, false, new Vector3(-2.5f, .5f, 6f), new Vector3(1.4f, 1f, 1f), new Color(.32f, .38f, .44f));
            T(root, PrimitiveType.Cube, "Station Cancel", 1004, InteractionKind.StationCancel, "Cancel Craft", "station_cancel", "anvil-1", "", "anvil", "", 1, false, new Vector3(-4.2f, .35f, 6f), new Vector3(.8f, .7f, .8f), new Color(.25f, .3f, .35f));
            T(root, PrimitiveType.Cylinder, "Cooking Station", 1005, InteractionKind.StationCraft, "Cook Raw Meat", "", "cooking-1", "", "cooking", "cook_raw_meat", 1, false, new Vector3(-2.5f, .5f, 8.2f), new Vector3(1.2f, .8f, 1.2f), new Color(.65f, .22f, .18f));
            T(root, PrimitiveType.Cube, "Farm Plant Plot", 1006, InteractionKind.FarmPlant, "Plant Potato", "farm_plant", "farm-plot-1", "", "", "", 1, false, new Vector3(0f, .12f, 8.5f), new Vector3(1.8f, .24f, 1.8f), new Color(.25f, .45f, .2f));
            T(root, PrimitiveType.Cube, "Farm Harvest Plot", 1007, InteractionKind.FarmHarvest, "Harvest Potato", "farm_harvest", "farm-plot-1", "", "", "", 1, false, new Vector3(2f, .12f, 8.5f), new Vector3(1.8f, .24f, 1.8f), new Color(.36f, .52f, .2f));
            T(root, PrimitiveType.Capsule, "Deer", 1008, InteractionKind.Animal, "Hunt Deer", "", "deer-1", "", "", "", 1, true, new Vector3(4.5f, .9f, 7.5f), new Vector3(.8f, 1.2f, .8f), new Color(.5f, .33f, .18f));
            T(root, PrimitiveType.Capsule, "Carcass", 1009, InteractionKind.Butcher, "Butcher Carcass", "butcher", "deer-carcass-1", "", "", "", 1, false, new Vector3(5.8f, .35f, 7.5f), new Vector3(.9f, .45f, .9f), new Color(.35f, .15f, .15f));
            T(root, PrimitiveType.Sphere, "Food Waste", 1010, InteractionKind.Clean, "Clean Waste", "clean", "waste-1", "", "", "", 1, false, new Vector3(1.2f, .3f, 10.5f), new Vector3(.55f, .55f, .55f), new Color(.22f, .55f, .32f));
            T(root, PrimitiveType.Cube, "Buyer Stall", 1011, InteractionKind.Buyer, "Open Buyer", "buyer_open", "buyer-1", "", "", "", 1, false, new Vector3(-4.5f, .8f, 9.6f), new Vector3(1.6f, 1.6f, .8f), new Color(.2f, .35f, .62f));
        }

        private static void T(Transform root, PrimitiveType primitive, string name, uint id, InteractionKind kind, string prompt, string type, string state, string resource, string station, string recipe, int max, bool primary, Vector3 pos, Vector3 scale, Color color)
        {
            GameObject go = GameObject.CreatePrimitive(primitive);
            go.name = name;
            go.transform.SetParent(root, false);
            go.transform.position = pos;
            go.transform.localScale = scale;
            go.AddComponent<InteractableTargetView>().Configure(kind, id, name, prompt, type, 0L, state, resource, station, recipe, max, primary);
            Renderer r = go.GetComponent<Renderer>();
            if (r != null)
            {
                r.material = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
                r.material.color = color;
            }
            GameObject label = new GameObject(name + " Label");
            label.transform.SetParent(go.transform, false);
            label.transform.localPosition = Vector3.up * 1.1f;
            TextMesh text = label.AddComponent<TextMesh>();
            text.text = name;
            text.characterSize = .18f;
            text.anchor = TextAnchor.MiddleCenter;
            text.alignment = TextAlignment.Center;
            text.color = Color.white;
        }
    }
}
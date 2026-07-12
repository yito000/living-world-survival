using FishNet.Managing;
using FishNet.Managing.Server;
using FishNet.Managing.Transporting;
using FishNet.Object;
using FishNet.Transporting.Tugboat;
using SurvivalWorld.Bootstrap;
using SurvivalWorld.Config;
using SurvivalWorld.Net;
using SurvivalWorld.Player;
using SurvivalWorld.Server;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace SurvivalWorld.Editor
{
    public static class M1SceneSetup
    {
        private const string ConfigPath = "Assets/Settings/SurvivalRuntimeConfig.asset";
        private const string PlayerControlsPath = "Assets/Settings/PlayerControls.inputactions";
        private const string BootstrapScenePath = "Assets/Scenes/Bootstrap.unity";
        private const string WorldScenePath = "Assets/Scenes/World_MVP.unity";
        private const string PlayerPrefabPath = "Assets/Prefabs/PlayerCharacter.prefab";

        [MenuItem("Survival World/Apply M1 Scene Setup")]
        public static void Apply()
        {
            SurvivalRuntimeConfig config = EnsureRuntimeConfig();
            InputActionAsset controls = AssetDatabase.LoadAssetAtPath<InputActionAsset>(PlayerControlsPath);
            SetupBootstrapScene(config);
            SetupWorldScene(config, controls);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        private static SurvivalRuntimeConfig EnsureRuntimeConfig()
        {
            var config = AssetDatabase.LoadAssetAtPath<SurvivalRuntimeConfig>(ConfigPath);
            if (config != null)
            {
                return config;
            }

            config = ScriptableObject.CreateInstance<SurvivalRuntimeConfig>();
            AssetDatabase.CreateAsset(config, ConfigPath);
            EditorUtility.SetDirty(config);
            return config;
        }

        private static void SetupBootstrapScene(SurvivalRuntimeConfig config)
        {
            Scene scene = EditorSceneManager.OpenScene(BootstrapScenePath, OpenSceneMode.Single);
            EnsureCameraAndLight();

            GameObject runtime = FindOrCreate("M1_Runtime");
            NetworkManager networkManager = EnsureComponent<NetworkManager>(runtime);
            Tugboat tugboat = EnsureComponent<Tugboat>(runtime);
            TransportManager transportManager = EnsureComponent<TransportManager>(runtime);
            ServerManager serverManager = EnsureComponent<ServerManager>(runtime);
            JoinTicketAuthenticator authenticator = EnsureComponent<JoinTicketAuthenticator>(runtime);
            NetworkSessionClient sessionClient = EnsureComponent<NetworkSessionClient>(runtime);
            ServerBootstrap serverBootstrap = EnsureComponent<ServerBootstrap>(runtime);
            Bootstrapper bootstrapper = EnsureComponent<Bootstrapper>(runtime);

            tugboat.SetPort(config.ServerPort);
            tugboat.SetMaximumClients(config.ServerCapacity);
            transportManager.Transport = tugboat;
            SetObjectReference(serverManager, "_authenticator", authenticator);
            SetObjectReference(sessionClient, "networkManager", networkManager);
            SetObjectReference(serverBootstrap, "config", config);
            SetObjectReference(serverBootstrap, "networkManager", networkManager);
            SetObjectReference(serverBootstrap, "authenticator", authenticator);
            SetObjectReference(bootstrapper, "config", config);
            SetObjectReference(bootstrapper, "sessionClient", sessionClient);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        private static void SetupWorldScene(SurvivalRuntimeConfig config, InputActionAsset controls)
        {
            Scene scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
            EnsureCameraAndLight();
            EnsureGround();

            GameObject player = FindOrCreate("PlayerCharacter_Prototype");
            player.transform.position = new Vector3(0f, 1f, 0f);
            player.transform.rotation = Quaternion.identity;
            player.transform.localScale = Vector3.one;

            EnsureComponent<NetworkObject>(player);
            EnsureComponent<CharacterController>(player);
            ThirdPersonInputReader inputReader = EnsureComponent<ThirdPersonInputReader>(player);
            NetworkPlayerController controller = EnsureComponent<NetworkPlayerController>(player);
            SetObjectReference(inputReader, "actionAsset", controls);
            SetObjectReference(controller, "inputReader", inputReader);

            Camera camera = Camera.main;
            if (camera != null)
            {
                ThirdPersonCameraRig cameraRig = EnsureComponent<ThirdPersonCameraRig>(camera.gameObject);
                cameraRig.Target = player.transform;
            }

            EnsurePrefabFolder();
            PrefabUtility.SaveAsPrefabAsset(player, PlayerPrefabPath);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        private static void EnsureGround()
        {
            GameObject ground = GameObject.Find("M1_Ground");
            if (ground == null)
            {
                ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
                ground.name = "M1_Ground";
            }

            ground.transform.position = Vector3.zero;
            ground.transform.localScale = new Vector3(8f, 1f, 8f);
        }

        private static void EnsureCameraAndLight()
        {
            if (Camera.main == null)
            {
                GameObject cameraObject = new GameObject("Main Camera");
                cameraObject.tag = "MainCamera";
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.transform.position = new Vector3(0f, 2.5f, -5f);
                camera.transform.rotation = Quaternion.Euler(20f, 0f, 0f);
            }

            if (FindFirst<Light>() == null)
            {
                GameObject lightObject = new GameObject("Directional Light");
                Light light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            }
        }

        private static GameObject FindOrCreate(string name)
        {
            GameObject existing = GameObject.Find(name);
            return existing != null ? existing : new GameObject(name);
        }

        private static T FindFirst<T>() where T : Object
        {
            T[] objects = Object.FindObjectsByType<T>(FindObjectsInactive.Include);
            return objects.Length == 0 ? null : objects[0];
        }

        private static T EnsureComponent<T>(GameObject gameObject) where T : Component
        {
            if (!gameObject.TryGetComponent(out T component))
            {
                component = gameObject.AddComponent<T>();
            }

            EditorUtility.SetDirty(component);
            return component;
        }

        private static void SetObjectReference(Object target, string propertyName, Object value)
        {
            if (target == null)
            {
                return;
            }

            SerializedObject serialized = new SerializedObject(target);
            SerializedProperty property = serialized.FindProperty(propertyName);
            if (property == null)
            {
                Debug.LogWarning($"Serialized property {propertyName} was not found on {target.name}.");
                return;
            }

            property.objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(target);
        }

        private static void EnsurePrefabFolder()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Prefabs"))
            {
                AssetDatabase.CreateFolder("Assets", "Prefabs");
            }
        }
    }
}


using FishNet.Object;
using StarterAssets;
using SurvivalWorld.Player;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SurvivalWorld.Editor
{
    public static class StarterAssetsPlayerPrefabSetup
    {
        private const string PlayerPrefabPath = "Assets/Prefabs/PlayerCharacter.prefab";
        private const string StarterInputPath = "Assets/StarterAssets/InputSystem/StarterAssets.inputactions";
        private const string StarterArmaturePrefabPath = "Assets/StarterAssets/ThirdPersonController/Prefabs/PlayerArmature.prefab";
        private const string StarterAnimatorControllerPath = "Assets/StarterAssets/ThirdPersonController/Character/Animations/StarterAssetsThirdPerson.controller";

        [MenuItem("Survival World/Apply StarterAssets Player Setup")]
        public static void Apply()
        {
            InputActionAsset inputActions = AssetDatabase.LoadAssetAtPath<InputActionAsset>(StarterInputPath);
            if (inputActions == null)
            {
                throw new MissingReferenceException("StarterAssets input actions were not found at " + StarterInputPath);
            }

            GameObject player = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);
            try
            {
                ConfigurePlayerObject(player, inputActions, replaceVisual: true);
                PrefabUtility.SaveAsPrefabAsset(player, PlayerPrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(player);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        public static void ConfigurePlayerObject(GameObject player, InputActionAsset inputActions, bool replaceVisual)
        {
            if (player == null)
            {
                throw new System.ArgumentNullException(nameof(player));
            }

            if (inputActions == null)
            {
                throw new System.ArgumentNullException(nameof(inputActions));
            }

            EnsureComponent<NetworkObject>(player);
            CharacterController characterController = EnsureComponent<CharacterController>(player);
            ConfigureCharacterController(characterController);

            StarterAssetsInputs starterInputs = EnsureComponent<StarterAssetsInputs>(player);
            starterInputs.cursorLocked = true;
            starterInputs.cursorInputForLook = true;
            UnityEditor.EditorUtility.SetDirty(starterInputs);

            PlayerInput playerInput = EnsureComponent<PlayerInput>(player);
            playerInput.actions = inputActions;
            playerInput.defaultActionMap = "Player";
            playerInput.notificationBehavior = PlayerNotifications.SendMessages;
            UnityEditor.EditorUtility.SetDirty(playerInput);

            ThirdPersonInputReader inputReader = EnsureComponent<ThirdPersonInputReader>(player);
            SetObjectReference(inputReader, "actionAsset", inputActions);
            SetObjectReference(inputReader, "starterAssetsInputs", starterInputs);
            SetString(inputReader, "actionMapName", "Player");
            SetString(inputReader, "moveActionName", "Move");
            SetString(inputReader, "lookActionName", "Look");
            SetString(inputReader, "jumpActionName", "Jump");
            SetString(inputReader, "sprintActionName", "Sprint");
            SetBool(inputReader, "preferStarterAssetsInputs", true);

            Transform visualRoot = EnsureChild(player.transform, "VisualRoot");
            visualRoot.localPosition = new Vector3(0f, -0.88f, 0f);
            visualRoot.localRotation = Quaternion.identity;
            visualRoot.localScale = Vector3.one;

            Animator animator = visualRoot.GetComponentInChildren<Animator>(true);
            Transform cameraTarget = visualRoot.Find("PlayerArmature/PlayerCameraRoot");
            if (replaceVisual)
            {
                ClearChildren(visualRoot);
                GameObject armature = InstantiateStarterArmature(visualRoot);
                animator = ConfigureArmatureVisual(armature);
                cameraTarget = FindChildRecursive(armature.transform, "PlayerCameraRoot");
            }

            NetworkPlayerController controller = EnsureComponent<NetworkPlayerController>(player);
            SetObjectReference(controller, "inputReader", inputReader);
            SetObjectReference(controller, "starterAssetsInputs", starterInputs);
            SetObjectReference(controller, "animator", animator);
            SetObjectReference(controller, "cameraTarget", cameraTarget);
            SetFloat(controller, "walkSpeed", 2f);
            SetFloat(controller, "sprintSpeed", 5.335f);
            SetFloat(controller, "jumpVelocity", 6f);
            SetFloat(controller, "gravity", -15f);
            SetFloat(controller, "speedChangeRate", 10f);
            SetFloat(controller, "fallTimeout", 0.15f);

            EnsureComponent<NetworkInventoryCommandBridge>(player);
            UnityEditor.EditorUtility.SetDirty(player);
        }

        private static GameObject InstantiateStarterArmature(Transform visualRoot)
        {
            GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(StarterArmaturePrefabPath);
            if (source == null)
            {
                throw new MissingReferenceException("StarterAssets PlayerArmature prefab was not found at " + StarterArmaturePrefabPath);
            }

            GameObject armature = PrefabUtility.InstantiatePrefab(source, visualRoot) as GameObject;
            if (armature == null)
            {
                armature = Object.Instantiate(source, visualRoot);
            }

            armature.name = "PlayerArmature";
            armature.transform.localPosition = Vector3.zero;
            armature.transform.localRotation = Quaternion.identity;
            armature.transform.localScale = Vector3.one;
            return armature;
        }

        private static Animator ConfigureArmatureVisual(GameObject armature)
        {
            DestroyIfExists<ThirdPersonController>(armature);
            DestroyIfExists<BasicRigidBodyPush>(armature);
            DestroyIfExists<PlayerInput>(armature);
            DestroyIfExists<StarterAssetsInputs>(armature);
            DestroyIfExists<CharacterController>(armature);

            Animator animator = armature.GetComponent<Animator>();
            if (animator == null)
            {
                animator = armature.AddComponent<Animator>();
            }

            RuntimeAnimatorController controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(StarterAnimatorControllerPath);
            if (controller == null)
            {
                throw new MissingReferenceException("StarterAssets animator controller was not found at " + StarterAnimatorControllerPath);
            }

            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;
            UnityEditor.EditorUtility.SetDirty(animator);
            return animator;
        }

        private static void ConfigureCharacterController(CharacterController characterController)
        {
            characterController.height = 1.8f;
            characterController.radius = 0.28f;
            characterController.center = new Vector3(0f, 0.93f, 0f);
            characterController.slopeLimit = 45f;
            characterController.stepOffset = 0.25f;
            characterController.skinWidth = 0.02f;
            characterController.minMoveDistance = 0f;
            UnityEditor.EditorUtility.SetDirty(characterController);
        }

        private static Transform EnsureChild(Transform parent, string name)
        {
            Transform existing = parent.Find(name);
            if (existing != null)
            {
                return existing;
            }

            var child = new GameObject(name);
            child.transform.SetParent(parent, worldPositionStays: false);
            return child.transform;
        }

        private static void ClearChildren(Transform parent)
        {
            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                Object.DestroyImmediate(parent.GetChild(i).gameObject);
            }
        }

        private static Transform FindChildRecursive(Transform parent, string name)
        {
            if (parent.name == name)
            {
                return parent;
            }

            for (int i = 0; i < parent.childCount; i++)
            {
                Transform result = FindChildRecursive(parent.GetChild(i), name);
                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }

        private static T EnsureComponent<T>(GameObject gameObject) where T : Component
        {
            if (!gameObject.TryGetComponent(out T component))
            {
                component = gameObject.AddComponent<T>();
            }

            UnityEditor.EditorUtility.SetDirty(component);
            return component;
        }

        private static void DestroyIfExists<T>(GameObject gameObject) where T : Component
        {
            T component = gameObject.GetComponent<T>();
            if (component != null)
            {
                Object.DestroyImmediate(component);
            }
        }

        private static void SetObjectReference(Object target, string propertyName, Object value)
        {
            SerializedProperty property = FindProperty(target, propertyName);
            if (property == null)
            {
                return;
            }

            property.objectReferenceValue = value;
            property.serializedObject.ApplyModifiedPropertiesWithoutUndo();
            UnityEditor.EditorUtility.SetDirty(target);
        }

        private static void SetString(Object target, string propertyName, string value)
        {
            SerializedProperty property = FindProperty(target, propertyName);
            if (property == null)
            {
                return;
            }

            property.stringValue = value;
            property.serializedObject.ApplyModifiedPropertiesWithoutUndo();
            UnityEditor.EditorUtility.SetDirty(target);
        }

        private static void SetBool(Object target, string propertyName, bool value)
        {
            SerializedProperty property = FindProperty(target, propertyName);
            if (property == null)
            {
                return;
            }

            property.boolValue = value;
            property.serializedObject.ApplyModifiedPropertiesWithoutUndo();
            UnityEditor.EditorUtility.SetDirty(target);
        }

        private static void SetFloat(Object target, string propertyName, float value)
        {
            SerializedProperty property = FindProperty(target, propertyName);
            if (property == null)
            {
                return;
            }

            property.floatValue = value;
            property.serializedObject.ApplyModifiedPropertiesWithoutUndo();
            UnityEditor.EditorUtility.SetDirty(target);
        }

        private static SerializedProperty FindProperty(Object target, string propertyName)
        {
            if (target == null)
            {
                return null;
            }

            SerializedObject serialized = new SerializedObject(target);
            SerializedProperty property = serialized.FindProperty(propertyName);
            if (property == null)
            {
                Debug.LogWarning($"Serialized property {propertyName} was not found on {target.name}.");
            }

            return property;
        }
    }
}

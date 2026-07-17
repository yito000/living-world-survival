using System;
using System.Collections.Generic;
using System.IO;
using SurvivalWorld.World;
using UnityEditor;
using UnityEngine;

namespace SurvivalWorld.Editor
{
    public sealed class AssetImportProcessor : AssetPostprocessor
    {
        private const string ImportedModelsFolder = "Assets/Generated/ImportedModels";
        private const string ClientPrefabFolder = "Assets/Generated/Prefabs/Client";
        private const string ServerPrefabFolder = "Assets/Generated/Prefabs/Server";
        private const string InteractionPointTag = "InteractionPoint";

        private static string ProjectRootPath => Directory.GetParent(Application.dataPath).FullName;

        private static string RepositoryRootPath => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", ".."));

        private static string DefaultManifestPath => Path.Combine(RepositoryRootPath, "build", "assets", "manifest.json");

        [MenuItem("Tools/Survival/Import Assets")]
        public static void ImportAllMenu()
        {
            ImportAll();
        }

        public static ImportAssetsResult ImportAll()
        {
            return ImportAll(DefaultManifestPath);
        }

        public static ImportAssetsResult ImportAll(string manifestPath)
        {
            if (string.IsNullOrWhiteSpace(manifestPath))
            {
                throw new ArgumentException("Manifest path is required.", nameof(manifestPath));
            }

            manifestPath = Path.GetFullPath(manifestPath);
            string sourceFolder = Path.GetDirectoryName(manifestPath);
            AssetManifest manifest = AssetManifest.Load(manifestPath);
            ValidateManifestShape(manifest);
            ValidateManifestFiles(manifest, sourceFolder);

            EnsureAssetFolder(ImportedModelsFolder);
            EnsureAssetFolder(ClientPrefabFolder);
            EnsureAssetFolder(ServerPrefabFolder);
            EnsureTagExists(InteractionPointTag);

            var result = new ImportAssetsResult();
            foreach (AssetManifestModule module in manifest.modules)
            {
                string modelAssetPath = CopyGlbIntoProject(sourceFolder, module);
                AssetDatabase.ImportAsset(modelAssetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

                GameObject modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(modelAssetPath);
                bool destroyModelAsset = false;
                if (modelAsset == null)
                {
                    Debug.LogWarning("Imported GLB did not produce a GameObject; generating deterministic manifest fallback mesh for " + modelAssetPath + ". Add a GLB importer package to use source model geometry.");
                    modelAsset = CreateManifestFallbackModel(module, manifest.module_size);
                    destroyModelAsset = true;
                }

                try
                {
                    SaveGeneratedPrefab(CreateClientPrefabInstanceForTesting(modelAsset, manifest, module), ClientPrefabFolder, module);
                    SaveGeneratedPrefab(CreateServerPrefabInstanceForTesting(modelAsset, manifest, module), ServerPrefabFolder, module);
                    result.ImportedModels++;
                    result.ClientPrefabs++;
                    result.ServerPrefabs++;
                }
                finally
                {
                    if (destroyModelAsset)
                    {
                        UnityEngine.Object.DestroyImmediate(modelAsset);
                    }
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            Debug.Log($"Imported {result.ImportedModels} manifest assets into {result.ClientPrefabs} client prefabs and {result.ServerPrefabs} server prefabs.");
            return result;
        }

        public static GameObject CreateClientPrefabInstanceForTesting(GameObject sourceModel, AssetManifest manifest, AssetManifestModule module)
        {
            return CreatePrefabInstance(sourceModel, manifest, module, includeRenderers: true);
        }

        public static GameObject CreateServerPrefabInstanceForTesting(GameObject sourceModel, AssetManifest manifest, AssetManifestModule module)
        {
            return CreatePrefabInstance(sourceModel, manifest, module, includeRenderers: false);
        }

        public static void ValidateManifestShape(AssetManifest manifest)
        {
            if (manifest == null)
            {
                throw new InvalidOperationException("Asset manifest is empty.");
            }

            if (manifest.module_size <= 0)
            {
                throw new InvalidOperationException("Asset manifest module_size must be positive.");
            }

            if (manifest.modules == null || manifest.modules.Length == 0)
            {
                throw new InvalidOperationException("Asset manifest modules are missing.");
            }

            foreach (AssetManifestModule module in manifest.modules)
            {
                ValidateRequired(module.asset_id, "asset_id", module);
                ValidateRequired(module.version, "version", module);
                ValidateRequired(module.kit, "kit", module);
                ValidateRequired(module.name, "name", module);
                ValidateRequired(module.glb, "glb", module);

                if (!module.has_collider)
                {
                    throw new InvalidOperationException($"Manifest module {ModuleLabel(module)} must declare has_collider=true.");
                }

                if (module.negative_scale)
                {
                    throw new InvalidOperationException($"Manifest module {ModuleLabel(module)} uses negative scale.");
                }

                if (module.triangles <= 0)
                {
                    throw new InvalidOperationException($"Manifest module {ModuleLabel(module)} must declare a positive triangle count.");
                }

                if (module.sockets == null)
                {
                    throw new InvalidOperationException($"Manifest module {ModuleLabel(module)} is missing sockets.");
                }

                if (module.interaction_points == null)
                {
                    throw new InvalidOperationException($"Manifest module {ModuleLabel(module)} is missing interaction_points.");
                }

                if (module.lods == null || module.lods.Length == 0)
                {
                    throw new InvalidOperationException($"Manifest module {ModuleLabel(module)} is missing lods.");
                }
            }
        }

        private void OnPreprocessModel()
        {
            if (!IsManagedModelAsset(assetPath))
            {
                return;
            }

            if (assetImporter is ModelImporter importer)
            {
                importer.globalScale = 1f;
                importer.importCameras = false;
                importer.importLights = false;
                importer.importAnimation = false;
                importer.isReadable = true;
            }
        }

        private void OnPostprocessModel(GameObject gameObject)
        {
            if (gameObject != null && IsManagedModelAsset(assetPath))
            {
                gameObject.name = Path.GetFileNameWithoutExtension(assetPath);
            }
        }

        private static GameObject CreatePrefabInstance(GameObject sourceModel, AssetManifest manifest, AssetManifestModule module, bool includeRenderers)
        {
            if (sourceModel == null)
            {
                throw new ArgumentNullException(nameof(sourceModel));
            }

            ValidateManifestShape(new AssetManifest { module_size = manifest.module_size, modules = new[] { module } });

            string prefabName = PrefabName(module);
            var root = new GameObject(prefabName);
            var metadata = root.AddComponent<GeneratedAssetMetadata>();
            metadata.Configure(module.asset_id, module.version, module.kit, module.name, manifest.module_size, !includeRenderers);

            GameObject modelInstance = InstantiateModel(sourceModel);
            modelInstance.name = "Model";
            modelInstance.transform.SetParent(root.transform, false);
            ResetTransform(modelInstance.transform);

            ApplyBottomCenterPivot(root, modelInstance);
            List<MeshFilter> colliderFilters = FindColliderFilters(modelInstance, module.has_collider);

            CreateSocketObjects(root, modelInstance, module, manifest.module_size);
            CreateInteractionPointObjects(root, modelInstance, module, manifest.module_size);

            if (includeRenderers)
            {
                DisableColliderRenderers(modelInstance);
                AttachClientColliders(colliderFilters);
                ConfigureLodGroup(root, modelInstance, module.lods);
            }
            else
            {
                AttachServerColliders(root, colliderFilters);
                AddNavMeshModifierIfAvailable(root);
                UnityEngine.Object.DestroyImmediate(modelInstance);
            }

            return root;
        }

        private static GameObject CreateManifestFallbackModel(AssetManifestModule module, int gridSize)
        {
            float size = Mathf.Max(1, gridSize);
            var root = new GameObject(PrefabName(module) + "_FallbackModel");

            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = PrefabName(module) + "_LOD0";
            body.transform.SetParent(root.transform, false);
            body.transform.localPosition = new Vector3(0f, size * 0.5f, 0f);
            body.transform.localScale = new Vector3(size, size, size);
            Collider bodyCollider = body.GetComponent<Collider>();
            if (bodyCollider != null)
            {
                UnityEngine.Object.DestroyImmediate(bodyCollider);
            }

            GameObject collider = GameObject.CreatePrimitive(PrimitiveType.Cube);
            collider.name = "UCX_" + PrefabName(module);
            collider.transform.SetParent(root.transform, false);
            collider.transform.localPosition = new Vector3(0f, size * 0.5f, 0f);
            collider.transform.localScale = new Vector3(size, size, size);
            Collider primitiveCollider = collider.GetComponent<Collider>();
            if (primitiveCollider != null)
            {
                UnityEngine.Object.DestroyImmediate(primitiveCollider);
            }

            foreach (string socketName in module.sockets)
            {
                var socket = new GameObject(socketName);
                socket.transform.SetParent(root.transform, false);
                socket.transform.localPosition = DefaultSocketPosition(socketName, gridSize);
            }

            foreach (string pointName in module.interaction_points)
            {
                var point = new GameObject(pointName);
                point.transform.SetParent(root.transform, false);
                point.transform.localPosition = DefaultInteractionPosition(pointName, gridSize);
            }

            return root;
        }
        private static GameObject InstantiateModel(GameObject sourceModel)
        {
            GameObject instance = null;
            if (PrefabUtility.GetPrefabAssetType(sourceModel) != PrefabAssetType.NotAPrefab)
            {
                instance = PrefabUtility.InstantiatePrefab(sourceModel) as GameObject;
            }

            if (instance == null)
            {
                instance = UnityEngine.Object.Instantiate(sourceModel);
            }

            return instance;
        }

        private static void ApplyBottomCenterPivot(GameObject root, GameObject modelInstance)
        {
            if (!TryGetRendererBounds(modelInstance, out Bounds bounds))
            {
                return;
            }

            Vector3 offset = new Vector3(-bounds.center.x, -bounds.min.y, -bounds.center.z);
            modelInstance.transform.localPosition += offset;
            root.transform.position = Vector3.zero;
            root.transform.rotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;
        }

        private static bool TryGetRendererBounds(GameObject modelInstance, out Bounds bounds)
        {
            bounds = default;
            Renderer[] renderers = modelInstance.GetComponentsInChildren<Renderer>(true);
            bool initialized = false;
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null)
                {
                    continue;
                }

                if (!initialized)
                {
                    bounds = renderer.bounds;
                    initialized = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return initialized;
        }

        private static List<MeshFilter> FindColliderFilters(GameObject modelInstance, bool colliderRequired)
        {
            var allFilters = new List<MeshFilter>();
            var colliderFilters = new List<MeshFilter>();
            foreach (MeshFilter filter in modelInstance.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter == null || filter.sharedMesh == null)
                {
                    continue;
                }

                allFilters.Add(filter);
                if (IsColliderNode(filter.gameObject.name))
                {
                    colliderFilters.Add(filter);
                }
            }

            if (colliderFilters.Count > 0)
            {
                return colliderFilters;
            }

            if (!colliderRequired)
            {
                return colliderFilters;
            }

            return allFilters;
        }

        private static void AttachClientColliders(List<MeshFilter> colliderFilters)
        {
            foreach (MeshFilter filter in colliderFilters)
            {
                MeshCollider meshCollider = filter.GetComponent<MeshCollider>();
                if (meshCollider == null)
                {
                    meshCollider = filter.gameObject.AddComponent<MeshCollider>();
                }

                meshCollider.sharedMesh = filter.sharedMesh;
                meshCollider.convex = false;
            }
        }

        private static void AttachServerColliders(GameObject root, List<MeshFilter> colliderFilters)
        {
            int index = 0;
            foreach (MeshFilter filter in colliderFilters)
            {
                var colliderObject = new GameObject("Collider_" + index + "_" + SanitizeName(filter.gameObject.name));
                colliderObject.transform.SetParent(root.transform, false);
                CopyRelativeTransform(root.transform, filter.transform, colliderObject.transform);

                MeshCollider meshCollider = colliderObject.AddComponent<MeshCollider>();
                meshCollider.sharedMesh = filter.sharedMesh;
                meshCollider.convex = false;
                index++;
            }

            if (index == 0)
            {
                throw new InvalidOperationException("Manifest requires a collider, but no MeshFilter was available to build one.");
            }
        }

        private static void DisableColliderRenderers(GameObject modelInstance)
        {
            foreach (Renderer renderer in modelInstance.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer != null && IsColliderNode(renderer.gameObject.name))
                {
                    renderer.enabled = false;
                }
            }
        }

        private static void ConfigureLodGroup(GameObject root, GameObject modelInstance, string[] lodNames)
        {
            Renderer[] allRenderers = NonColliderRenderers(modelInstance);
            if (allRenderers.Length == 0)
            {
                return;
            }

            var lods = new List<LOD>();
            if (lodNames != null)
            {
                for (int i = 0; i < lodNames.Length; i++)
                {
                    Renderer[] renderers = RenderersForLod(modelInstance, lodNames[i]);
                    if (renderers.Length == 0 && i == 0)
                    {
                        renderers = allRenderers;
                    }

                    if (renderers.Length == 0)
                    {
                        continue;
                    }

                    lods.Add(new LOD(ScreenRelativeTransition(i), renderers));
                }
            }

            if (lods.Count == 0)
            {
                lods.Add(new LOD(0.6f, allRenderers));
            }

            LODGroup lodGroup = root.AddComponent<LODGroup>();
            lodGroup.SetLODs(lods.ToArray());
            lodGroup.RecalculateBounds();
        }

        private static Renderer[] NonColliderRenderers(GameObject modelInstance)
        {
            var renderers = new List<Renderer>();
            foreach (Renderer renderer in modelInstance.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer != null && !IsColliderNode(renderer.gameObject.name))
                {
                    renderers.Add(renderer);
                }
            }

            return renderers.ToArray();
        }

        private static Renderer[] RenderersForLod(GameObject modelInstance, string lodName)
        {
            if (string.IsNullOrWhiteSpace(lodName))
            {
                return Array.Empty<Renderer>();
            }

            var renderers = new List<Renderer>();
            foreach (Renderer renderer in modelInstance.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer != null && !IsColliderNode(renderer.gameObject.name) && HasAncestorNamed(renderer.transform, lodName))
                {
                    renderers.Add(renderer);
                }
            }

            return renderers.ToArray();
        }

        private static bool HasAncestorNamed(Transform transform, string name)
        {
            for (Transform current = transform; current != null; current = current.parent)
            {
                if (string.Equals(current.name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static float ScreenRelativeTransition(int index)
        {
            if (index <= 0)
            {
                return 0.6f;
            }

            if (index == 1)
            {
                return 0.3f;
            }

            return Mathf.Max(0.01f, 0.15f / index);
        }

        private static void CreateSocketObjects(GameObject root, GameObject modelInstance, AssetManifestModule module, int gridSize)
        {
            foreach (string socketName in module.sockets)
            {
                if (string.IsNullOrWhiteSpace(socketName))
                {
                    throw new InvalidOperationException($"Manifest module {ModuleLabel(module)} contains an empty socket name.");
                }

                GameObject socket = CreatePointObject(root, modelInstance, socketName, DefaultSocketPosition(socketName, gridSize));
                socket.AddComponent<GeneratedSocket>().Configure(socketName);
            }
        }

        private static void CreateInteractionPointObjects(GameObject root, GameObject modelInstance, AssetManifestModule module, int gridSize)
        {
            foreach (string pointName in module.interaction_points)
            {
                if (string.IsNullOrWhiteSpace(pointName))
                {
                    throw new InvalidOperationException($"Manifest module {ModuleLabel(module)} contains an empty interaction point name.");
                }

                GameObject point = CreatePointObject(root, modelInstance, pointName, DefaultInteractionPosition(pointName, gridSize));
                point.tag = InteractionPointTag;
                point.AddComponent<GeneratedInteractionPoint>().Configure(pointName);
            }
        }

        private static GameObject CreatePointObject(GameObject root, GameObject modelInstance, string pointName, Vector3 fallbackPosition)
        {
            var point = new GameObject(pointName);
            point.transform.SetParent(root.transform, false);

            Transform sourcePoint = FindChildByName(modelInstance.transform, pointName);
            if (sourcePoint != null)
            {
                CopyRelativeTransform(root.transform, sourcePoint, point.transform);
            }
            else
            {
                point.transform.localPosition = fallbackPosition;
                point.transform.localRotation = Quaternion.identity;
                point.transform.localScale = Vector3.one;
            }

            return point;
        }

        private static Transform FindChildByName(Transform root, string name)
        {
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
            {
                if (string.Equals(transform.name, name, StringComparison.Ordinal))
                {
                    return transform;
                }
            }

            return null;
        }

        private static Vector3 DefaultSocketPosition(string socketName, int gridSize)
        {
            float half = gridSize * 0.5f;
            string lower = socketName.ToLowerInvariant();
            if (lower.Contains("top"))
            {
                return new Vector3(0f, gridSize, 0f);
            }

            if (lower.Contains("bottom"))
            {
                return Vector3.zero;
            }

            if (lower.Contains("front"))
            {
                return new Vector3(0f, half, half);
            }

            if (lower.Contains("back"))
            {
                return new Vector3(0f, half, -half);
            }

            if (lower.Contains("left"))
            {
                return new Vector3(-half, half, 0f);
            }

            if (lower.Contains("right"))
            {
                return new Vector3(half, half, 0f);
            }

            return Vector3.zero;
        }

        private static Vector3 DefaultInteractionPosition(string pointName, int gridSize)
        {
            float half = gridSize * 0.5f;
            string lower = pointName.ToLowerInvariant();
            if (lower.Contains("top"))
            {
                return new Vector3(0f, gridSize, 0f);
            }

            return new Vector3(0f, 1f, half);
        }

        private static void CopyRelativeTransform(Transform relativeRoot, Transform source, Transform target)
        {
            Matrix4x4 matrix = relativeRoot.worldToLocalMatrix * source.localToWorldMatrix;
            target.localPosition = matrix.GetColumn(3);

            Vector3 right = matrix.GetColumn(0);
            Vector3 up = matrix.GetColumn(1);
            Vector3 forward = matrix.GetColumn(2);
            target.localScale = new Vector3(right.magnitude, up.magnitude, forward.magnitude);

            if (up.sqrMagnitude > 0.0001f && forward.sqrMagnitude > 0.0001f)
            {
                target.localRotation = Quaternion.LookRotation(forward.normalized, up.normalized);
            }
            else
            {
                target.localRotation = Quaternion.identity;
            }
        }

        private static void AddNavMeshModifierIfAvailable(GameObject root)
        {
            Type modifierType = Type.GetType("Unity.AI.Navigation.NavMeshModifier, Unity.AI.Navigation");
            if (modifierType == null || root.GetComponent(modifierType) != null)
            {
                return;
            }

            root.AddComponent(modifierType);
        }

        private static void SaveGeneratedPrefab(GameObject root, string folder, AssetManifestModule module)
        {
            try
            {
                string assetPath = folder + "/" + PrefabName(module) + ".prefab";
                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, assetPath);
                if (prefab == null)
                {
                    throw new InvalidOperationException("Failed to save generated prefab: " + assetPath);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static string CopyGlbIntoProject(string sourceFolder, AssetManifestModule module)
        {
            string sourcePath = Path.Combine(sourceFolder, module.glb);
            string targetAssetPath = ImportedModelsFolder + "/" + Path.GetFileName(module.glb).Replace('\\', '/');
            string targetPath = AssetPathToFullPath(targetAssetPath);

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
            if (!FileContentEquals(sourcePath, targetPath))
            {
                File.Copy(sourcePath, targetPath, overwrite: true);
            }

            return targetAssetPath;
        }

        private static bool FileContentEquals(string leftPath, string rightPath)
        {
            if (!File.Exists(rightPath))
            {
                return false;
            }

            var leftInfo = new FileInfo(leftPath);
            var rightInfo = new FileInfo(rightPath);
            if (leftInfo.Length != rightInfo.Length)
            {
                return false;
            }

            byte[] left = File.ReadAllBytes(leftPath);
            byte[] right = File.ReadAllBytes(rightPath);
            if (left.Length != right.Length)
            {
                return false;
            }

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static void ValidateManifestFiles(AssetManifest manifest, string sourceFolder)
        {
            if (string.IsNullOrWhiteSpace(sourceFolder) || !Directory.Exists(sourceFolder))
            {
                throw new DirectoryNotFoundException("Asset manifest source folder not found: " + sourceFolder);
            }

            foreach (AssetManifestModule module in manifest.modules)
            {
                string glbPath = Path.Combine(sourceFolder, module.glb);
                if (!File.Exists(glbPath))
                {
                    throw new FileNotFoundException($"Manifest module {ModuleLabel(module)} references missing GLB: {glbPath}", glbPath);
                }
            }
        }

        private static void ValidateRequired(string value, string fieldName, AssetManifestModule module)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"Manifest module {ModuleLabel(module)} is missing {fieldName}.");
            }
        }

        private static bool IsManagedModelAsset(string candidateAssetPath)
        {
            string normalized = (candidateAssetPath ?? string.Empty).Replace('\\', '/');
            return normalized.StartsWith(ImportedModelsFolder + "/", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsColliderNode(string name)
        {
            return !string.IsNullOrWhiteSpace(name) && name.StartsWith("UCX_", StringComparison.OrdinalIgnoreCase);
        }

        private static void ResetTransform(Transform transform)
        {
            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;
            transform.localScale = Vector3.one;
        }

        private static void EnsureAssetFolder(string folder)
        {
            string normalized = folder.Replace('\\', '/').Trim('/');
            string[] parts = normalized.Split('/');
            if (parts.Length == 0 || parts[0] != "Assets")
            {
                throw new InvalidOperationException("Unity asset folder must start with Assets: " + folder);
            }

            string current = "Assets";
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }

                current = next;
            }
        }

        private static void EnsureTagExists(string tag)
        {
            UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            if (assets == null || assets.Length == 0)
            {
                return;
            }

            var tagManager = new SerializedObject(assets[0]);
            SerializedProperty tags = tagManager.FindProperty("tags");
            if (tags == null)
            {
                return;
            }

            for (int i = 0; i < tags.arraySize; i++)
            {
                if (tags.GetArrayElementAtIndex(i).stringValue == tag)
                {
                    return;
                }
            }

            int index = tags.arraySize;
            tags.InsertArrayElementAtIndex(index);
            tags.GetArrayElementAtIndex(index).stringValue = tag;
            tagManager.ApplyModifiedPropertiesWithoutUndo();
        }

        private static string AssetPathToFullPath(string assetPath)
        {
            string normalized = assetPath.Replace('/', Path.DirectorySeparatorChar);
            return Path.GetFullPath(Path.Combine(ProjectRootPath, normalized));
        }

        private static string PrefabName(AssetManifestModule module)
        {
            return SanitizeName(module.kit) + "_" + SanitizeName(module.name);
        }

        private static string SanitizeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "unnamed";
            }

            char[] chars = value.Trim().ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '_' && chars[i] != '-')
                {
                    chars[i] = '_';
                }
            }

            return new string(chars);
        }

        private static string ModuleLabel(AssetManifestModule module)
        {
            if (module == null)
            {
                return "<null>";
            }

            string kit = string.IsNullOrWhiteSpace(module.kit) ? "?" : module.kit;
            string name = string.IsNullOrWhiteSpace(module.name) ? "?" : module.name;
            return kit + "/" + name;
        }

        [Serializable]
        public sealed class AssetManifest
        {
            public int module_size;
            public int seed;
            public AssetManifestModule[] modules;

            public static AssetManifest Load(string path)
            {
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException("Asset manifest not found: " + path, path);
                }

                return JsonUtility.FromJson<AssetManifest>(File.ReadAllText(path));
            }
        }

        [Serializable]
        public sealed class AssetManifestModule
        {
            public string asset_id;
            public string glb;
            public bool has_collider;
            public string[] interaction_points;
            public string kit;
            public string[] lods;
            public string name;
            public bool negative_scale;
            public string[] sockets;
            public int triangles;
            public string version;
        }

        public sealed class ImportAssetsResult
        {
            public int ImportedModels { get; set; }
            public int ClientPrefabs { get; set; }
            public int ServerPrefabs { get; set; }
        }
    }
}

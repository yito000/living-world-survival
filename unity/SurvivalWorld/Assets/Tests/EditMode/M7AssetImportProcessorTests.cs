using NUnit.Framework;
using SurvivalWorld.Editor;
using SurvivalWorld.World;
using UnityEngine;

namespace SurvivalWorld.Tests
{
    public sealed class M7AssetImportProcessorTests
    {
        [Test]
        public void ClientPrefabIncludesManifestMetadataInteractionSocketColliderAndLod()
        {
            GameObject source = CreateSourceModel();
            GameObject root = null;
            try
            {
                root = AssetImportProcessor.CreateClientPrefabInstanceForTesting(source, Manifest(), Module());

                GeneratedAssetMetadata metadata = root.GetComponent<GeneratedAssetMetadata>();
                Assert.IsNotNull(metadata);
                Assert.AreEqual("asset-test", metadata.AssetId);
                Assert.IsFalse(metadata.ServerPrefab);
                Assert.IsNotNull(root.GetComponent<LODGroup>());
                Assert.Greater(root.GetComponentsInChildren<Renderer>(true).Length, 0);
                Assert.Greater(root.GetComponentsInChildren<MeshCollider>(true).Length, 0);

                GeneratedSocket socket = root.GetComponentInChildren<GeneratedSocket>(true);
                Assert.IsNotNull(socket);
                Assert.AreEqual("socket_top", socket.SocketName);
                Assert.AreEqual(new Vector3(0f, 4f, 0f), socket.transform.localPosition);

                GeneratedInteractionPoint point = root.GetComponentInChildren<GeneratedInteractionPoint>(true);
                Assert.IsNotNull(point);
                Assert.AreEqual("ip_use", point.InteractionId);
                Assert.AreEqual("InteractionPoint", point.tag);
            }
            finally
            {
                Destroy(root);
                Destroy(source);
            }
        }

        [Test]
        public void ServerPrefabOmitsRenderersButKeepsColliderMetadataAndInteraction()
        {
            GameObject source = CreateSourceModel();
            GameObject root = null;
            try
            {
                root = AssetImportProcessor.CreateServerPrefabInstanceForTesting(source, Manifest(), Module());

                GeneratedAssetMetadata metadata = root.GetComponent<GeneratedAssetMetadata>();
                Assert.IsNotNull(metadata);
                Assert.IsTrue(metadata.ServerPrefab);
                Assert.AreEqual(0, root.GetComponentsInChildren<Renderer>(true).Length);
                Assert.Greater(root.GetComponentsInChildren<MeshCollider>(true).Length, 0);
                Assert.IsNotNull(root.GetComponentInChildren<GeneratedInteractionPoint>(true));
            }
            finally
            {
                Destroy(root);
                Destroy(source);
            }
        }

        [Test]
        public void ManifestShapeValidationRejectsMissingRequiredFields()
        {
            var manifest = Manifest();
            manifest.modules[0].asset_id = string.Empty;

            Assert.Throws<System.InvalidOperationException>(() => AssetImportProcessor.ValidateManifestShape(manifest));
        }

        private static AssetImportProcessor.AssetManifest Manifest()
        {
            return new AssetImportProcessor.AssetManifest
            {
                module_size = 4,
                seed = 1,
                modules = new[] { Module() }
            };
        }

        private static AssetImportProcessor.AssetManifestModule Module()
        {
            return new AssetImportProcessor.AssetManifestModule
            {
                asset_id = "asset-test",
                glb = "test.glb",
                has_collider = true,
                interaction_points = new[] { "ip_use" },
                kit = "test",
                lods = new[] { "LOD0" },
                name = "crate",
                negative_scale = false,
                sockets = new[] { "socket_top" },
                triangles = 12,
                version = "1.0.0"
            };
        }

        private static GameObject CreateSourceModel()
        {
            var root = new GameObject("SourceModel");
            GameObject lod = GameObject.CreatePrimitive(PrimitiveType.Cube);
            lod.name = "LOD0";
            lod.transform.SetParent(root.transform, false);
            Object.DestroyImmediate(lod.GetComponent<Collider>());
            return root;
        }

        private static void Destroy(GameObject gameObject)
        {
            if (gameObject != null)
            {
                Object.DestroyImmediate(gameObject);
            }
        }
    }
}

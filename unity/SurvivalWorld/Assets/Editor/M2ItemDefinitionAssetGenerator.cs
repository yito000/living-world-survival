using System.IO;
using SurvivalWorld.Items;
using UnityEditor;
using UnityEngine;

namespace SurvivalWorld.Editor
{
    public static class M2ItemDefinitionAssetGenerator
    {
        private const string ItemDataFolder = "Assets/Data/Items";

        [MenuItem("Survival World/M2/Generate Item Definitions")]
        public static void GenerateDefaultAssets()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Data"))
            {
                AssetDatabase.CreateFolder("Assets", "Data");
            }

            if (!AssetDatabase.IsValidFolder(ItemDataFolder))
            {
                AssetDatabase.CreateFolder("Assets/Data", "Items");
            }

            foreach (ItemDefinitionData data in ItemDefinitionCatalog.MvpDefinitions)
            {
                string assetPath = Path.Combine(ItemDataFolder, data.ItemDefinitionId + ".asset").Replace('\\', '/');
                ItemDefinition asset = AssetDatabase.LoadAssetAtPath<ItemDefinition>(assetPath);
                if (asset == null)
                {
                    asset = ScriptableObject.CreateInstance<ItemDefinition>();
                    asset.Configure(data);
                    AssetDatabase.CreateAsset(asset, assetPath);
                }
                else
                {
                    asset.Configure(data);
                    EditorUtility.SetDirty(asset);
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }
    }
}

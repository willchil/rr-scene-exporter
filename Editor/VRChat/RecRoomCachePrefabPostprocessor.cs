using UnityEditor;
using UnityEngine;

namespace RRSceneExporter.VRChat
{
    public class RecRoomCachePrefabPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets, string[] deletedAssets,
            string[] movedAssets, string[] movedFromAssetPaths)
        {
            int cleaned = 0;
            foreach (string path in importedAssets)
            {
                if (!path.StartsWith("Assets/RecRoomCache/", System.StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!path.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase))
                    continue;

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null)
                    continue;

                // Open the prefab for editing, strip missing scripts, save
                string assetPath = AssetDatabase.GetAssetPath(prefab);
                using (var scope = new PrefabUtility.EditPrefabContentsScope(assetPath))
                {
                    var root = scope.prefabContentsRoot;
                    int removed = RemoveMissingScriptsRecursive(root);
                    if (removed > 0)
                    {
                        cleaned++;
                        Debug.Log($"[RRCachePostprocessor] Stripped {removed} missing scripts from {path}");
                    }
                }
            }

            if (cleaned > 0)
                Debug.Log($"[RRCachePostprocessor] Cleaned {cleaned} prefabs in RecRoomCache.");
        }

        private static int RemoveMissingScriptsRecursive(GameObject go)
        {
            int count = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);
            for (int i = 0; i < go.transform.childCount; i++)
                count += RemoveMissingScriptsRecursive(go.transform.GetChild(i).gameObject);
            return count;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using RecRoom.Protobuf;
using Debug = UnityEngine.Debug;

namespace CompositeSceneGenerator
{
    internal static class DependencyCache
    {
        internal static HashSet<Guid> CollectUsedPrefabGuids(PersistedRoomData room)
        {
            var guids = new HashSet<Guid>();
            foreach (var view in room.PersistenceViews)
                CollectGuidsFromView(view, guids);
            return guids;
        }

        private static void CollectGuidsFromView(PersistenceViewData view, HashSet<Guid> guids)
        {
            if (view.SpawnableToolData != null && !view.SpawnableToolData.PrefabId.IsEmpty)
            {
                Guid g = PrefabResolver.ByteStringToGuid(view.SpawnableToolData.PrefabId);
                if (g != Guid.Empty)
                    guids.Add(g);
            }
            foreach (var child in view.ChildViews)
            {
                if (child.Data != null)
                    CollectGuidsFromView(child.Data, guids);
            }
        }

        internal static void CachePackageDependencies(Dictionary<Guid, GameObject> prefabLookup, HashSet<Guid> usedGuids)
        {
            const string cacheDir = "Assets/RecRoomCache";
            string cacheFullPath = Path.Combine(Application.dataPath, "RecRoomCache");

            // Collect used prefab paths and all their package dependencies
            var prefabPaths = new HashSet<string>();
            var allDepPaths = new HashSet<string>();
            foreach (var kvp in prefabLookup)
            {
                if (!usedGuids.Contains(kvp.Key))
                    continue;

                string prefabPath = AssetDatabase.GetAssetPath(kvp.Value);
                if (!prefabPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                    continue;

                prefabPaths.Add(prefabPath);
                foreach (string dep in AssetDatabase.GetDependencies(prefabPath, true))
                {
                    if (dep.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                        allDepPaths.Add(dep);
                }
            }

            if (prefabPaths.Count == 0)
                return;

            // Non-prefab deps: meshes, textures, materials, shaders, etc.
            var nonPrefabDeps = new HashSet<string>(allDepPaths);
            nonPrefabDeps.ExceptWith(prefabPaths);

            // 1. Copy non-prefab deps WITHOUT .meta → Unity assigns fresh GUIDs.
            int copiedDeps = 0;
            foreach (string depPath in nonPrefabDeps)
            {
                if (depPath.EndsWith(".shader", StringComparison.OrdinalIgnoreCase)
                    || depPath.EndsWith(".hlsl", StringComparison.OrdinalIgnoreCase)
                    || depPath.EndsWith(".cginc", StringComparison.OrdinalIgnoreCase))
                    continue;

                string rel = depPath.Substring("Packages/".Length);
                string destAsset = Path.Combine(cacheFullPath, rel);
                if (File.Exists(destAsset))
                    continue;

                string srcAsset = AssetUtility.ResolvePackageFilePath(depPath);
                if (srcAsset == null || !File.Exists(srcAsset))
                    continue;

                Directory.CreateDirectory(Path.GetDirectoryName(destAsset));
                File.Copy(srcAsset, destAsset, true);
                copiedDeps++;
            }

            if (copiedDeps > 0)
            {
                AssetDatabase.Refresh();
                Debug.Log($"[CompositeScene] Cached {copiedDeps} package dependencies to {cacheDir} (fresh GUIDs).");
            }

            // 2. Build path map: package asset path → cached asset path
            var shaderExts = new[] { ".shader", ".hlsl", ".cginc" };
            var pathMap = new Dictionary<string, string>();
            foreach (string depPath in nonPrefabDeps)
            {
                if (shaderExts.Any(ext => depPath.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                    continue;

                string rel = depPath.Substring("Packages/".Length);
                string cachedPath = cacheDir + "/" + rel;
                if (!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(cachedPath)))
                    pathMap[depPath] = cachedPath;
            }

            // 2b. Remap references inside cached non-prefab assets (materials → shaders, etc.)
            int remappedAssets = 0;
            foreach (var entry in pathMap)
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(entry.Value);
                if (asset == null) continue;
                if (RemapAssetRefs(asset, pathMap))
                    remappedAssets++;

                foreach (var sub in AssetDatabase.LoadAllAssetsAtPath(entry.Value))
                {
                    if (sub != null && sub != asset)
                        RemapAssetRefs(sub, pathMap);
                }
            }
            if (remappedAssets > 0)
            {
                AssetDatabase.SaveAssets();
                Debug.Log($"[CompositeScene] Remapped references in {remappedAssets} cached dependency assets.");
            }

            // 2c. Remap Rec Room shaders to standard URP shaders on cached materials
            ShaderRemapper.RemapRecRoomShaders(pathMap);

            // 3. Create cached prefabs: instantiate original, strip Rec Room scripts, remap refs, save as new prefab
            int createdPrefabs = 0;
            foreach (var kvp in prefabLookup)
            {
                if (!usedGuids.Contains(kvp.Key))
                    continue;

                string pkgPrefabPath = AssetDatabase.GetAssetPath(kvp.Value);
                if (!prefabPaths.Contains(pkgPrefabPath))
                    continue;

                string rel = pkgPrefabPath.Substring("Packages/".Length);
                string cachedPath = cacheDir + "/" + rel;

                if (AssetDatabase.LoadAssetAtPath<GameObject>(cachedPath) != null)
                    continue;

                string cachedDir = Path.GetDirectoryName(cachedPath).Replace('\\', '/');
                AssetUtility.EnsureAssetFolderExists(cachedDir);

                var instance = UnityEngine.Object.Instantiate(kvp.Value);

                // Strip MonoBehaviours from Rec Room packages
                foreach (var mb in instance.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb == null) continue;
                    string asmName = mb.GetType().Assembly.GetName().Name;
                    if (asmName.StartsWith("RecRoom.", StringComparison.OrdinalIgnoreCase)
                        || asmName.StartsWith("RecRoom-", StringComparison.OrdinalIgnoreCase))
                        UnityEngine.Object.DestroyImmediate(mb, true);
                }

                // Remove any remaining missing-script components
                foreach (var t in instance.GetComponentsInChildren<Transform>(true))
                    GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);

                // Remap asset references from package paths to cached paths
                foreach (var comp in instance.GetComponentsInChildren<Component>(true))
                {
                    if (comp == null) continue;
                    RemapAssetRefs(comp, pathMap);
                }

                PrefabUtility.SaveAsPrefabAsset(instance, cachedPath);
                UnityEngine.Object.DestroyImmediate(instance);
                createdPrefabs++;
            }

            if (createdPrefabs > 0)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                Debug.Log($"[CompositeScene] Created {createdPrefabs} cached prefabs in {cacheDir}.");
            }

            // 4. Redirect prefab lookup to cached copies
            var updates = new List<KeyValuePair<Guid, GameObject>>();
            foreach (var kvp in prefabLookup)
            {
                if (!usedGuids.Contains(kvp.Key))
                    continue;

                string pkgPrefabPath = AssetDatabase.GetAssetPath(kvp.Value);
                if (!prefabPaths.Contains(pkgPrefabPath))
                    continue;

                string rel = pkgPrefabPath.Substring("Packages/".Length);
                string cachedPath = cacheDir + "/" + rel;
                var localPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(cachedPath);
                if (localPrefab != null)
                    updates.Add(new KeyValuePair<Guid, GameObject>(kvp.Key, localPrefab));
            }

            foreach (var u in updates)
                prefabLookup[u.Key] = u.Value;

            if (updates.Count > 0)
                Debug.Log($"[CompositeScene] Redirected {updates.Count} prefab entries to cached copies.");
        }

        internal static bool RemapAssetRefs(UnityEngine.Object asset, Dictionary<string, string> pathMap)
        {
            var so = new SerializedObject(asset);
            bool modified = false;
            var prop = so.GetIterator();
            while (prop.Next(true))
            {
                if (prop.propertyType != SerializedPropertyType.ObjectReference)
                    continue;
                if (prop.objectReferenceValue == null)
                    continue;

                string refPath = AssetDatabase.GetAssetPath(prop.objectReferenceValue);
                if (string.IsNullOrEmpty(refPath) || !pathMap.TryGetValue(refPath, out string cachedPath))
                    continue;

                var replacement = FindEquivalentAsset(prop.objectReferenceValue, cachedPath);
                if (replacement != null)
                {
                    prop.objectReferenceValue = replacement;
                    modified = true;
                }
            }
            if (modified)
                so.ApplyModifiedPropertiesWithoutUndo();
            return modified;
        }

        private static UnityEngine.Object FindEquivalentAsset(UnityEngine.Object original, string cachedPath)
        {
            string origPath = AssetDatabase.GetAssetPath(original);

            if (AssetDatabase.LoadMainAssetAtPath(origPath) == original)
                return AssetDatabase.LoadMainAssetAtPath(cachedPath);

            var targetType = original.GetType();
            string targetName = original.name;
            foreach (var sub in AssetDatabase.LoadAllAssetsAtPath(cachedPath))
            {
                if (sub != null && sub.GetType() == targetType && sub.name == targetName)
                    return sub;
            }

            return null;
        }
    }
}

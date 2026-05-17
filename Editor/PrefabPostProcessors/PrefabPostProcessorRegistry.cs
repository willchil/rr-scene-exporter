using System;
using System.Collections.Generic;
using UnityEngine;
using RecRoom.Protobuf;

namespace CompositeSceneGenerator
{
    /// <summary>
    /// Registry that dispatches post-processing to the correct handler based on prefab GUID.
    /// </summary>
    internal static class PrefabPostProcessorRegistry
    {
        private static readonly Dictionary<Guid, IPrefabPostProcessor> s_processors
            = new Dictionary<Guid, IPrefabPostProcessor>();

        private static bool s_initialized;

        private static void EnsureInitialized()
        {
            if (s_initialized)
                return;
            s_initialized = true;

            Register(new LightPostProcessor());
            Register(new ReplicatorPostProcessor());
        }

        private static void Register(IPrefabPostProcessor processor)
        {
            foreach (var id in processor.HandledPrefabIds)
                s_processors[id] = processor;
        }

        /// <summary>
        /// Runs PreparePrefab on each cached prefab that has a registered
        /// post-processor.  Call once after the prefab lookup is finalized.
        /// </summary>
        internal static void PreparePrefabs(Dictionary<Guid, GameObject> prefabLookup)
        {
            EnsureInitialized();
            var prepared = new HashSet<IPrefabPostProcessor>();
            foreach (var kvp in prefabLookup)
            {
                if (!s_processors.TryGetValue(kvp.Key, out var processor))
                    continue;

                string path = UnityEditor.AssetDatabase.GetAssetPath(kvp.Value);
                if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/"))
                    continue;

                // Load the prefab asset for editing
                var prefabRoot = UnityEditor.PrefabUtility.LoadPrefabContents(path);
                processor.PreparePrefab(prefabRoot, kvp.Key);
                UnityEditor.PrefabUtility.SaveAsPrefabAsset(prefabRoot, path);
                UnityEditor.PrefabUtility.UnloadPrefabContents(prefabRoot);
            }
        }

        /// <summary>
        /// If a post-processor exists for this prefab GUID, run it on the instance.
        /// </summary>
        internal static void TryProcess(GameObject instance, Guid prefabGuid, PersistenceViewData view)
        {
            EnsureInitialized();
            if (s_processors.TryGetValue(prefabGuid, out var processor))
                processor.Process(instance, prefabGuid, view);
        }
    }
}

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
        }

        private static void Register(IPrefabPostProcessor processor)
        {
            foreach (var id in processor.HandledPrefabIds)
                s_processors[id] = processor;
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

using System;
using System.Collections.Generic;
using UnityEngine;
using RecRoom.Protobuf;

namespace CompositeSceneGenerator
{
    /// <summary>
    /// Post-processor for the Rec Room Replicator prefab.
    /// Deactivates the Replicator instance entirely so its preview mesh
    /// and any reparented children stay hidden in the composite scene.
    /// </summary>
    internal class ReplicatorPostProcessor : IPrefabPostProcessor
    {
        static readonly Guid ReplicatorId = new Guid("a901e043-df41-434b-a4e6-943d338faeac");

        private static readonly HashSet<Guid> s_ids = new HashSet<Guid> { ReplicatorId };

        public IReadOnlyCollection<Guid> HandledPrefabIds => s_ids;

        public void PreparePrefab(GameObject prefabRoot, Guid prefabGuid)
        {
            // No prefab-asset edits needed; we deactivate per-instance in Process.
        }

        public void Process(GameObject instance, Guid prefabGuid, PersistenceViewData view)
        {
            if (instance != null)
                instance.SetActive(false);
        }
    }
}

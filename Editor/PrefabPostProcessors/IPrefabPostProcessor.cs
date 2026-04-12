using System;
using System.Collections.Generic;
using UnityEngine;
using RecRoom.Protobuf;

namespace CompositeSceneGenerator
{
    /// <summary>
    /// Interface for post-processing specific Rec Room prefab types after placement.
    /// Implementations handle remapping Rec Room data to Unity components.
    /// </summary>
    internal interface IPrefabPostProcessor
    {
        /// <summary>The set of Rec Room prefab GUIDs this processor handles.</summary>
        IReadOnlyCollection<Guid> HandledPrefabIds { get; }

        /// <summary>
        /// Called after a prefab instance is placed in the scene.
        /// </summary>
        /// <param name="instance">The instantiated GameObject.</param>
        /// <param name="prefabGuid">The Rec Room prefab GUID.</param>
        /// <param name="view">The persistence data for this instance.</param>
        void Process(GameObject instance, Guid prefabGuid, PersistenceViewData view);
    }
}

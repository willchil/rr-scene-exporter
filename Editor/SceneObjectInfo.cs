using System;
using UnityEngine;

namespace CompositeSceneGenerator
{
    /// <summary>
    /// World-space transform of a persistence view as captured from a
    /// RecRoomObjects scene, plus its parent's uniqueId for hierarchy rebuild.
    /// </summary>
    public struct SceneObjectInfo
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 LossyScale;
        public Guid ParentId;
    }
}

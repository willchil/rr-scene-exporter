using System;
using UnityEngine;

namespace CompositeSceneGenerator
{
    /// <summary>
    /// World-space transform of a persistence view as captured from a
    /// RecRoomObjects scene, plus its parent's uniqueId for hierarchy rebuild
    /// and any deformation scale (Rooms 2 equivalent of SandboxDeformationData).
    /// </summary>
    public struct SceneObjectInfo
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 LossyScale;
        public Guid ParentId;
        /// <summary>Non-uniform deformation multiplier; Vector3.one if no deformation.</summary>
        public Vector3 Deformation;
        /// <summary>GameObject name from the RecRoomObjects scene.</summary>
        public string Name;
    }
}

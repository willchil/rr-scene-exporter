using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using RecRoom.Protobuf;
using Debug = UnityEngine.Debug;

namespace CompositeSceneGenerator
{
    internal static class ObjectPlacer
    {
        internal static void PlaceView(
            PersistenceViewData view, Transform parent,
            Dictionary<Guid, GameObject> prefabLookup, Scene scene,
            ref int placed, ref int skipped,
            Dictionary<Guid, SceneObjectInfo> sceneTransforms = null,
            Dictionary<Guid, GameObject> placedInstances = null)
        {
            if (view.SpawnableToolData != null && !view.SpawnableToolData.PrefabId.IsEmpty)
            {
                Guid prefabGuid = PrefabResolver.ByteStringToGuid(view.SpawnableToolData.PrefabId);
                if (prefabGuid != Guid.Empty && prefabLookup.TryGetValue(prefabGuid, out GameObject prefab))
                {
                    if (placed < 3)
                        Debug.Log($"[PlaceView] Instantiating from: {AssetDatabase.GetAssetPath(prefab)}");
                    var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                    if (instance != null)
                    {
                        instance.transform.SetParent(parent, true);
                        ApplyViewTransform(instance.transform, view, sceneTransforms);
                        if (HiddenPrefabIds.Ids.Contains(prefabGuid))
                            DisableRenderers(instance);
                        DisableCollidersIfDecoration(instance, view);
                        AddRigidbodyIfPhysical(instance, view);
                        PrefabPostProcessorRegistry.TryProcess(instance, prefabGuid, view);
                        RecordPlacedInstance(placedInstances, view, instance);
                        placed++;

                        foreach (var child in view.ChildViews)
                        {
                            if (child.Data != null)
                                PlaceView(child.Data, instance.transform,
                                    prefabLookup, scene, ref placed, ref skipped, sceneTransforms, placedInstances);
                        }
                        return;
                    }
                }
                else if (prefabGuid != Guid.Empty)
                {
                    Debug.LogWarning($"[PlaceView] Skipped: prefab GUID {prefabGuid} not found in lookup.");
                }
            }

            foreach (var child in view.ChildViews)
            {
                if (child.Data != null)
                    PlaceView(child.Data, parent,
                        prefabLookup, scene, ref placed, ref skipped, sceneTransforms, placedInstances);
            }

            if (view.SpawnableToolData != null && !view.SpawnableToolData.PrefabId.IsEmpty)
                skipped++;
        }

        internal static void PlaceConnectableNode(
            ConnectableNodeData node, Transform parent,
            Dictionary<string, PersistenceViewData> viewById,
            Dictionary<Guid, GameObject> prefabLookup, Scene scene,
            HashSet<string> placedViewIds, ref int placed, ref int skipped,
            Dictionary<Guid, SceneObjectInfo> sceneTransforms = null,
            Dictionary<Guid, GameObject> placedInstances = null)
        {
            Transform nodeTransform = parent;

            PersistenceViewData view = null;
            string viewKey = null;
            if (node.HasPersistenceId && !node.PersistenceId.IsEmpty)
            {
                viewKey = node.PersistenceId.ToBase64();
                viewById.TryGetValue(viewKey, out view);
            }

            if (viewKey != null)
                placedViewIds.Add(viewKey);

            if (view != null && view.SpawnableToolData != null && !view.SpawnableToolData.PrefabId.IsEmpty)
            {
                Guid prefabGuid = PrefabResolver.ByteStringToGuid(view.SpawnableToolData.PrefabId);
                if (prefabGuid != Guid.Empty && prefabLookup.TryGetValue(prefabGuid, out GameObject prefab))
                {
                    var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                    if (instance != null)
                    {
                        instance.transform.SetParent(parent, false);

                        if (HiddenPrefabIds.Ids.Contains(prefabGuid))
                            DisableRenderers(instance);
                        DisableCollidersIfDecoration(instance, view);
                        AddRigidbodyIfPhysical(instance, view);
                        PrefabPostProcessorRegistry.TryProcess(instance, prefabGuid, view);

                        if (node.IsRoot)
                        {
                            ApplyViewTransform(instance.transform, view, sceneTransforms);
                        }
                        else if (view.Transform == null && TryGetSceneTransform(view, sceneTransforms, out var sceneInfo))
                        {
                            ApplySceneTransform(instance.transform, sceneInfo);
                        }
                        else
                        {
                            if (node.PositionRelativeToParent != null)
                                instance.transform.localPosition = new Vector3(
                                    node.PositionRelativeToParent.X,
                                    node.PositionRelativeToParent.Y,
                                    node.PositionRelativeToParent.Z);

                            if (node.RotationRelativeToParent != null)
                                instance.transform.localRotation = Quaternion.Euler(
                                    node.RotationRelativeToParent.X,
                                    node.RotationRelativeToParent.Y,
                                    node.RotationRelativeToParent.Z);

                            float uniformScale = (view.Transform != null && view.Transform.Scale != 0)
                                ? view.Transform.Scale : 1f;
                            if (view.SandboxDeformationData?.Deformation != null)
                            {
                                var d = view.SandboxDeformationData.Deformation;
                                instance.transform.localScale = new Vector3(
                                    uniformScale * d.X, uniformScale * d.Y, uniformScale * d.Z);
                            }
                            else
                            {
                                instance.transform.localScale = Vector3.one * uniformScale;
                            }
                        }

                        nodeTransform = instance.transform;
                        RecordPlacedInstance(placedInstances, view, instance);
                        placed++;
                    }
                    else
                    {
                        skipped++;
                    }
                }
                else
                {
                    Debug.LogWarning($"[PlaceConnectable] Skipped: prefab GUID {prefabGuid} not found in lookup.");
                    skipped++;
                }
            }

            foreach (var child in node.Children)
            {
                PlaceConnectableNode(child, nodeTransform,
                    viewById, prefabLookup, scene, placedViewIds, ref placed, ref skipped, sceneTransforms, placedInstances);
            }
        }

        private static void RecordPlacedInstance(
            Dictionary<Guid, GameObject> placedInstances,
            PersistenceViewData view, GameObject instance)
        {
            if (placedInstances == null || view == null || view.Id == null || view.Id.IsEmpty)
                return;
            var guid = PrefabResolver.ByteStringToGuid(view.Id);
            if (guid == Guid.Empty)
                return;
            placedInstances[guid] = instance;
        }

        /// <summary>
        /// For every scene-captured GameObject (with a uniqueId) that isn't a
        /// placed view, instantiate an empty container GameObject so its
        /// transform is preserved. Children placed later (via reparenting) will
        /// land under these containers instead of skipping over them.
        /// </summary>
        internal static void MaterializeContainers(
            Dictionary<Guid, GameObject> placedInstances,
            Dictionary<Guid, SceneObjectInfo> sceneTransforms,
            Transform defaultParent,
            Scene scene)
        {
            if (placedInstances == null || sceneTransforms == null)
                return;

            int created = 0;
            foreach (var kvp in sceneTransforms)
            {
                if (placedInstances.ContainsKey(kvp.Key))
                    continue;

                var info = kvp.Value;
                var go = new GameObject(string.IsNullOrEmpty(info.Name) ? "Container" : info.Name);
                SceneManager.MoveGameObjectToScene(go, scene);
                if (defaultParent != null)
                    go.transform.SetParent(defaultParent, false);
                ApplySceneTransform(go.transform, info);
                placedInstances[kvp.Key] = go;
                created++;
            }

            if (created > 0)
                Debug.Log($"[ObjectPlacer] Materialized {created} container GameObjects from RecRoomObjects scene.");
        }

        /// <summary>
        /// Reparent placed instances based on the parent chain captured from the
        /// RecRoomObjects scene. Walks up through non-placed intermediates (e.g.
        /// a Replicator's "Container" child) until a placed instance is reached.
        /// Preserves world position/rotation/scale.
        /// </summary>
        internal static void ReparentFromSceneHierarchy(
            Dictionary<Guid, GameObject> placedInstances,
            Dictionary<Guid, SceneObjectInfo> sceneTransforms)
        {
            if (placedInstances == null || sceneTransforms == null)
                return;

            int reparented = 0;
            foreach (var kvp in placedInstances)
            {
                if (kvp.Value == null)
                    continue;
                if (!sceneTransforms.TryGetValue(kvp.Key, out var info))
                    continue;

                // Walk up the captured parent chain through non-placed
                // intermediates until we find a placed instance.
                Guid parentId = info.ParentId;
                GameObject parentInstance = null;
                var visited = new HashSet<Guid> { kvp.Key };
                while (parentId != Guid.Empty && visited.Add(parentId))
                {
                    if (placedInstances.TryGetValue(parentId, out parentInstance) && parentInstance != null)
                        break;
                    parentInstance = null;
                    if (!sceneTransforms.TryGetValue(parentId, out var parentInfo))
                        break;
                    parentId = parentInfo.ParentId;
                }

                if (parentInstance == null || kvp.Value.transform.parent == parentInstance.transform)
                    continue;

                kvp.Value.transform.SetParent(parentInstance.transform, true);
                reparented++;
            }

            if (reparented > 0)
                Debug.Log($"[ObjectPlacer] Reparented {reparented} instances from RecRoomObjects scene hierarchy.");
        }

        /// <summary>
        /// Apply a transform to <paramref name="transform"/>, preferring the protobuf
        /// TransformData when present and falling back to the RecRoomObjects scene
        /// transform (Rooms 2 case, where transforms live in DOTSBI not in the binpb).
        /// </summary>
        internal static void ApplyViewTransform(
            Transform transform, PersistenceViewData view,
            Dictionary<Guid, SceneObjectInfo> sceneTransforms)
        {
            if (view.Transform != null)
            {
                ApplyTransform(transform, view.Transform, view.SandboxDeformationData);
                return;
            }

            if (TryGetSceneTransform(view, sceneTransforms, out var info))
                ApplySceneTransform(transform, info);
        }

        private static bool TryGetSceneTransform(
            PersistenceViewData view,
            Dictionary<Guid, SceneObjectInfo> sceneTransforms,
            out SceneObjectInfo info)
        {
            info = default;
            if (sceneTransforms == null || view == null || view.Id == null || view.Id.IsEmpty)
                return false;
            var guid = PrefabResolver.ByteStringToGuid(view.Id);
            if (guid == Guid.Empty)
                return false;
            return sceneTransforms.TryGetValue(guid, out info);
        }

        private static void ApplySceneTransform(Transform transform, SceneObjectInfo info)
        {
            // Scene-derived transforms are in world space; place the instance
            // there regardless of parent.
            transform.position = info.Position;
            transform.rotation = info.Rotation;

            // Compensate for the parent's world scale so that the instance's
            // effective world scale matches the captured lossy scale, then fold
            // in the deformation multiplier (Rooms 2 equivalent of
            // SandboxDeformationData applied at root, matching Rooms 1 behavior).
            var parentLossy = transform.parent != null ? transform.parent.lossyScale : Vector3.one;
            var d = info.Deformation == Vector3.zero ? Vector3.one : info.Deformation;
            transform.localScale = new Vector3(
                SafeDivide(info.LossyScale.x, parentLossy.x) * d.x,
                SafeDivide(info.LossyScale.y, parentLossy.y) * d.y,
                SafeDivide(info.LossyScale.z, parentLossy.z) * d.z);
        }

        private static float SafeDivide(float a, float b)
        {
            return Mathf.Approximately(b, 0f) ? a : a / b;
        }

        internal static void ApplyTransform(Transform transform, TransformData data, SandboxDeformationData deformation = null)
        {
            if (data == null)
                return;

            if (data.Position != null)
                transform.localPosition = new Vector3(data.Position.X, data.Position.Y, data.Position.Z);

            if (data.QuaternionRotation != null)
                transform.localRotation = new Quaternion(
                    data.QuaternionRotation.X,
                    data.QuaternionRotation.Y,
                    data.QuaternionRotation.Z,
                    data.QuaternionRotation.W);

            float uniformScale = data.Scale != 0 ? data.Scale : 1f;
            if (deformation?.Deformation != null)
            {
                var d = deformation.Deformation;
                transform.localScale = new Vector3(
                    uniformScale * d.X,
                    uniformScale * d.Y,
                    uniformScale * d.Z);
            }
            else
            {
                transform.localScale = Vector3.one * uniformScale;
            }
        }

        internal static void DisableRenderers(GameObject go)
        {
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                r.enabled = false;
        }

        private const int PhysicsModeDecoration = 1;
        private const int PhysicsModePhysical = 4;

        internal static void DisableCollidersIfDecoration(GameObject go, PersistenceViewData view)
        {
            if (view?.ShapeContainerData == null)
                return;
            if (view.ShapeContainerData.PhysicsMode != PhysicsModeDecoration)
                return;

            foreach (var c in go.GetComponentsInChildren<Collider>(true))
                c.enabled = false;
        }

        internal static void AddRigidbodyIfPhysical(GameObject go, PersistenceViewData view)
        {
            if (view?.ShapeContainerData == null)
                return;
            if (view.ShapeContainerData.PhysicsMode != PhysicsModePhysical)
                return;
            if (go.GetComponent<Rigidbody>() != null)
                return;

            var rb = go.AddComponent<Rigidbody>();
            rb.useGravity = true;
            rb.isKinematic = false;
        }
    }
}

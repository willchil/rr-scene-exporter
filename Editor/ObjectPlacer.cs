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
            ref int placed, ref int skipped)
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
                        ApplyTransform(instance.transform, view.Transform, view.SandboxDeformationData);
                        if (HiddenPrefabIds.Ids.Contains(prefabGuid))
                            DisableRenderers(instance);
                        DisableCollidersIfDecoration(instance, view);
                        AddRigidbodyIfPhysical(instance, view);
                        PrefabPostProcessorRegistry.TryProcess(instance, prefabGuid, view);
                        placed++;

                        foreach (var child in view.ChildViews)
                        {
                            if (child.Data != null)
                                PlaceView(child.Data, instance.transform,
                                    prefabLookup, scene, ref placed, ref skipped);
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
                        prefabLookup, scene, ref placed, ref skipped);
            }

            if (view.SpawnableToolData != null && !view.SpawnableToolData.PrefabId.IsEmpty)
                skipped++;
        }

        internal static void PlaceConnectableNode(
            ConnectableNodeData node, Transform parent,
            Dictionary<string, PersistenceViewData> viewById,
            Dictionary<Guid, GameObject> prefabLookup, Scene scene,
            HashSet<string> placedViewIds, ref int placed, ref int skipped)
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
                            ApplyTransform(instance.transform, view.Transform, view.SandboxDeformationData);
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
                    viewById, prefabLookup, scene, placedViewIds, ref placed, ref skipped);
            }
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

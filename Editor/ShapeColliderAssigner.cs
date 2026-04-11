using System;
using System.Collections.Generic;
using UnityEngine;
using RecRoom.Protobuf;
using Debug = UnityEngine.Debug;

namespace CompositeSceneGenerator
{
    internal static class ShapeColliderAssigner
    {
        // physics_mode values observed in Rec Room save data:
        //   0 = Environment (default, has colliders)
        //   1 = Decoration (no colliders)
        //   2 = Physical
        //   3 = Sticky
        //   4 = Picker/interactive objects
        // Only mode 1 (decoration) should skip colliders.
        private const int PhysicsModeDecoration = 1;

        /// <summary>
        /// Builds a mapping from ShapeContainer GUID (no hyphens, lowercase) to physics_mode
        /// by inspecting the PersistenceViewData entries that carry ShapeContainerData.
        /// </summary>
        internal static Dictionary<string, int> BuildPhysicsModeMap(PersistedRoomData roomData)
        {
            var map = new Dictionary<string, int>();
            foreach (var view in roomData.PersistenceViews)
            {
                if (view.ShapeContainerData == null || view.ShapeContainerData.ShapeCollection == null)
                    continue;

                if (view.Id == null || view.Id.IsEmpty)
                    continue;

                Guid guid = PrefabResolver.ByteStringToGuid(view.Id);
                if (guid == Guid.Empty)
                    continue;

                string key = guid.ToString("N"); // no hyphens, lowercase
                map[key] = view.ShapeContainerData.PhysicsMode;
            }
            return map;
        }

        /// <summary>
        /// Assigns colliders to shape meshes under the MakerPen hierarchy.
        /// Expects: MakerPen → (FBX root) → ShapeContainerRoot → ShapeContainer_{guid} → Shape_{type}_{guid}
        /// </summary>
        internal static void AssignColliders(GameObject makerPenRoot, PersistedRoomData roomData)
        {
            var physicsModeMap = BuildPhysicsModeMap(roomData);

            // Find ShapeContainerRoot — it may be directly under makerPenRoot or one level deeper (FBX instance)
            Transform containerRoot = FindShapeContainerRoot(makerPenRoot.transform);
            if (containerRoot == null)
            {
                // Log the actual hierarchy to help diagnose
                Debug.LogWarning("[ShapeColliders] Could not find ShapeContainerRoot under MakerPen hierarchy. " +
                    $"Root has {makerPenRoot.transform.childCount} children.");
                for (int c = 0; c < makerPenRoot.transform.childCount; c++)
                {
                    var child = makerPenRoot.transform.GetChild(c);
                    Debug.LogWarning($"[ShapeColliders]   Child: '{child.name}' ({child.childCount} children)");
                    for (int g = 0; g < Mathf.Min(child.childCount, 5); g++)
                        Debug.LogWarning($"[ShapeColliders]     Grandchild: '{child.GetChild(g).name}'");
                }
                return;
            }

            int totalColliders = 0;
            int skippedDecoration = 0;
            int containers = 0;

            for (int i = 0; i < containerRoot.childCount; i++)
            {
                Transform containerTransform = containerRoot.GetChild(i);
                string containerName = containerTransform.name;

                // Expected format: ShapeContainer_{32-char hex guid}
                if (!containerName.StartsWith("ShapeContainer_"))
                    continue;

                string guidStr = containerName.Substring("ShapeContainer_".Length);
                containers++;

                // Look up physics mode; default to environment (1) if not found
                int physicsMode = 1;
                if (physicsModeMap.TryGetValue(guidStr, out int pm))
                    physicsMode = pm;

                if (physicsMode == PhysicsModeDecoration)
                {
                    skippedDecoration += containerTransform.childCount;
                    continue;
                }

                for (int j = 0; j < containerTransform.childCount; j++)
                {
                    Transform shapeTransform = containerTransform.GetChild(j);
                    if (AddColliderToShape(shapeTransform))
                        totalColliders++;
                }
            }

            Debug.Log($"[ShapeColliders] Processed {containers} shape containers. " +
                      $"Added {totalColliders} colliders, skipped {skippedDecoration} decoration shapes.");
        }

        private static Transform FindShapeContainerRoot(Transform root)
        {
            // The ShapeContainerRoot node from the GLB may be collapsed during
            // FBX conversion or Unity import. Find the nearest ancestor whose
            // children include "ShapeContainer_" entries.
            return FindParentOfShapeContainers(root, 0, 5);
        }

        private static Transform FindParentOfShapeContainers(Transform parent, int depth, int maxDepth)
        {
            if (depth >= maxDepth)
                return null;

            // Check if this node's children include ShapeContainer_ entries
            for (int i = 0; i < parent.childCount; i++)
            {
                if (parent.GetChild(i).name.StartsWith("ShapeContainer_"))
                    return parent;
            }

            // Recurse into children
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform found = FindParentOfShapeContainers(parent.GetChild(i), depth + 1, maxDepth);
                if (found != null)
                    return found;
            }
            return null;
        }

        private static bool AddColliderToShape(Transform shapeTransform)
        {
            string name = shapeTransform.name;

            // Already has a collider — skip
            if (shapeTransform.GetComponent<Collider>() != null)
                return false;

            MeshFilter mf = shapeTransform.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null)
                return false;

            if (IsCurveShape(name))
            {
                var mc = shapeTransform.gameObject.AddComponent<MeshCollider>();
                mc.convex = false;
                return true;
            }

            // All other shapes get a box collider approximation
            shapeTransform.gameObject.AddComponent<BoxCollider>();
            return true;
        }

        private static bool IsCurveShape(string name)
        {
            // Shape names follow the pattern: Shape_CURVE_Tube_{guid} or Shape_CURVE_Ribbon_{guid}
            return name.StartsWith("Shape_CURVE_", StringComparison.Ordinal);
        }
    }
}

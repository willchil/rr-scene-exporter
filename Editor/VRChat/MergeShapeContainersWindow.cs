using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace RRSceneExporter.VRChat
{
    public class MergeShapeContainersWindow : EditorWindow
    {
        private GameObject _sourceObject;
        private float _maxMeshSize = 0f;

        [MenuItem("Rec Room/Merge shape container shapes")]
        private static void ShowWindow()
        {
            GetWindow<MergeShapeContainersWindow>("Merge Shape Containers");
        }

        private void OnGUI()
        {
            _sourceObject = (GameObject)EditorGUILayout.ObjectField(
                "Source Object", _sourceObject, typeof(GameObject), true);
            _maxMeshSize = EditorGUILayout.FloatField("Max Mesh Size", _maxMeshSize);

            EditorGUI.BeginDisabledGroup(_sourceObject == null);
            if (GUILayout.Button("Merge"))
                Merge(_sourceObject, _maxMeshSize);
            EditorGUI.EndDisabledGroup();
        }

        private const string ContainerPrefix = "SHAPE_CONTAINER_";

        private static void Merge(GameObject source, float maxMeshSize)
        {
            string sourceName = source.name;
            string meshFolderName = sourceName + "-Optimized";
            string meshFolder = "Assets/" + meshFolderName;
            string prefabPath = meshFolder + "/" + sourceName + "-Optimized.prefab";

            if (!AssetDatabase.IsValidFolder(meshFolder))
                AssetDatabase.CreateFolder("Assets", meshFolderName);

            Transform containerRoot = FindParentOfShapeContainers(source.transform, 0, 5);
            if (containerRoot == null)
            {
                EditorUtility.DisplayDialog("Merge",
                    "No SHAPE_CONTAINER_ nodes found in the source hierarchy.", "OK");
                return;
            }

            int totalContainers = 0;
            for (int i = 0; i < containerRoot.childCount; i++)
            {
                if (containerRoot.GetChild(i).name.StartsWith(ContainerPrefix, StringComparison.OrdinalIgnoreCase))
                    totalContainers++;
            }

            GameObject root = new GameObject(sourceName);
            root.transform.localPosition = source.transform.localPosition;
            root.transform.localRotation = source.transform.localRotation;
            root.transform.localScale = source.transform.localScale;
            int mergedCount = 0;
            int processed = 0;

            try
            {
                for (int i = 0; i < containerRoot.childCount; i++)
                {
                    Transform container = containerRoot.GetChild(i);
                    if (!container.name.StartsWith(ContainerPrefix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    processed++;
                    EditorUtility.DisplayProgressBar("Merging Shape Containers",
                        $"Processing {container.name}... ({processed}/{totalContainers})",
                        (float)processed / totalContainers);

                    if (container.childCount == 0)
                        continue;

                    // Collect shape info from the ORIGINAL before duplicating
                    var shapes = new List<ShapeInfo>();
                    for (int j = 0; j < container.childCount; j++)
                    {
                        Transform shape = container.GetChild(j);
                        var mf = shape.GetComponent<MeshFilter>();
                        var mr = shape.GetComponent<MeshRenderer>();
                        if (mf == null || mf.sharedMesh == null || mr == null)
                            continue;

                        shapes.Add(new ShapeInfo
                        {
                            Transform = shape,
                            MeshFilter = mf,
                            MeshRenderer = mr,
                        });
                    }

                    if (shapes.Count == 0)
                        continue;

                    // Determine spatial partitions
                    var cellMap = new Dictionary<Vector3Int, List<int>>();
                    if (maxMeshSize > 0)
                    {
                        for (int s = 0; s < shapes.Count; s++)
                        {
                            Vector3 localPos = container.InverseTransformPoint(
                                shapes[s].Transform.position);
                            var cell = new Vector3Int(
                                Mathf.FloorToInt(localPos.x / maxMeshSize),
                                Mathf.FloorToInt(localPos.y / maxMeshSize),
                                Mathf.FloorToInt(localPos.z / maxMeshSize));
                            if (!cellMap.ContainsKey(cell))
                                cellMap[cell] = new List<int>();
                            cellMap[cell].Add(s);
                        }
                    }

                    bool useSubmeshes = cellMap.Count > 1;

                    // Duplicate the container — preserves all components, static
                    // flags, layer, tag, Rigidbody, etc. on the container and
                    // all children.
                    GameObject containerObj = Instantiate(container.gameObject);
                    containerObj.name = container.name;
                    containerObj.transform.SetParent(root.transform, false);
                    SetRelativeTransform(containerObj.transform, container, source.transform);

                    // Use the first shape's renderer as a reference for settings
                    MeshRenderer refRenderer = shapes[0].MeshRenderer;

                    // Strip MeshFilter/MeshRenderer from the cloned hierarchy
                    StripRenderers(containerObj.transform);

                    if (!useSubmeshes)
                    {
                        // Single combined mesh on the container (original behavior)
                        Mesh combinedMesh = CombineMeshesByMaterial(
                            shapes, container, out Material[] materials);

                        if (combinedMesh == null)
                        {
                            DestroyImmediate(containerObj);
                            continue;
                        }

                        combinedMesh.name = container.name;
                        string safeName = SanitizeFileName(container.name);
                        AssetDatabase.CreateAsset(combinedMesh,
                            meshFolder + "/" + safeName + ".asset");

                        PruneEmptyChildren(containerObj.transform);

                        containerObj.AddComponent<MeshFilter>().sharedMesh = combinedMesh;
                        var newRenderer = containerObj.AddComponent<MeshRenderer>();
                        newRenderer.sharedMaterials = materials;
                        CopyRendererSettings(refRenderer, newRenderer);
                    }
                    else
                    {
                        // Build name → cloned child map for reparenting
                        var clonedChildMap = new Dictionary<string, Transform>();
                        for (int j = 0; j < containerObj.transform.childCount; j++)
                        {
                            var child = containerObj.transform.GetChild(j);
                            clonedChildMap[child.name] = child;
                        }

                        int submeshIdx = 0;
                        foreach (var kvp in cellMap)
                        {
                            var cellShapes = new List<ShapeInfo>();
                            var cellShapeNames = new HashSet<string>();
                            foreach (int idx in kvp.Value)
                            {
                                cellShapes.Add(shapes[idx]);
                                cellShapeNames.Add(shapes[idx].Transform.name);
                            }

                            Mesh cellMesh = CombineMeshesByMaterial(
                                cellShapes, container, out Material[] cellMats);
                            if (cellMesh == null) continue;

                            string submeshName = $"Submesh_{submeshIdx}";
                            cellMesh.name = $"{container.name}_{submeshName}";
                            AssetDatabase.CreateAsset(cellMesh,
                                meshFolder + "/" + SanitizeFileName(cellMesh.name) + ".asset");

                            // Create submesh root under the container
                            var submeshObj = new GameObject(submeshName);
                            submeshObj.transform.SetParent(containerObj.transform, false);
                            GameObjectUtility.SetStaticEditorFlags(submeshObj,
                                GameObjectUtility.GetStaticEditorFlags(containerObj));

                            // Reparent relevant cloned children under the submesh
                            foreach (string shapeName in cellShapeNames)
                            {
                                if (clonedChildMap.TryGetValue(shapeName, out Transform clonedChild))
                                    clonedChild.SetParent(submeshObj.transform, true);
                            }

                            submeshObj.AddComponent<MeshFilter>().sharedMesh = cellMesh;
                            var newRenderer = submeshObj.AddComponent<MeshRenderer>();
                            newRenderer.sharedMaterials = cellMats;
                            CopyRendererSettings(refRenderer, newRenderer);

                            submeshIdx++;
                        }

                        PruneEmptyChildren(containerObj.transform);
                    }

                    mergedCount++;
                }

                var savedPrefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                AssetDatabase.SaveAssets();

                // Select the saved prefab in the Project tab
                Selection.activeObject = savedPrefab;
                EditorGUIUtility.PingObject(savedPrefab);

                Debug.Log($"[MergeShapes] Merged {mergedCount} containers into {prefabPath}");
                EditorUtility.DisplayDialog("Merge",
                    $"Merged {mergedCount} shape containers.\nPrefab: {prefabPath}", "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                DestroyImmediate(root);
            }
        }

        private static void CopyRendererSettings(MeshRenderer src, MeshRenderer dst)
        {
            dst.shadowCastingMode = src.shadowCastingMode;
            dst.receiveShadows = src.receiveShadows;
            dst.lightProbeUsage = src.lightProbeUsage;
            dst.reflectionProbeUsage = src.reflectionProbeUsage;
            dst.motionVectorGenerationMode = src.motionVectorGenerationMode;
            dst.allowOcclusionWhenDynamic = src.allowOcclusionWhenDynamic;
            dst.rendererPriority = src.rendererPriority;
            dst.lightmapIndex = src.lightmapIndex;
            dst.realtimeLightmapIndex = src.realtimeLightmapIndex;
            dst.lightmapScaleOffset = src.lightmapScaleOffset;
            dst.realtimeLightmapScaleOffset = src.realtimeLightmapScaleOffset;
            dst.probeAnchor = src.probeAnchor;
            dst.lightProbeProxyVolumeOverride = src.lightProbeProxyVolumeOverride;
            dst.sortingLayerID = src.sortingLayerID;
            dst.sortingOrder = src.sortingOrder;
        }

        private static void SetRelativeTransform(
            Transform target, Transform original, Transform relativeTo)
        {
            target.localPosition = relativeTo.InverseTransformPoint(original.position);
            target.localRotation = Quaternion.Inverse(relativeTo.rotation) * original.rotation;

            Vector3 parentScale = relativeTo.lossyScale;
            Vector3 childScale = original.lossyScale;
            target.localScale = new Vector3(
                childScale.x / Mathf.Max(parentScale.x, 1e-6f),
                childScale.y / Mathf.Max(parentScale.y, 1e-6f),
                childScale.z / Mathf.Max(parentScale.z, 1e-6f));
        }

        private static Mesh CombineMeshesByMaterial(
            List<ShapeInfo> shapes, Transform container, out Material[] materials)
        {
            var groups = new Dictionary<Material, List<CombineInstance>>();
            var materialOrder = new List<Material>();

            foreach (var shape in shapes)
            {
                Matrix4x4 matrix =
                    container.worldToLocalMatrix * shape.Transform.localToWorldMatrix;
                Material[] mats = shape.MeshRenderer.sharedMaterials;
                Mesh mesh = shape.MeshFilter.sharedMesh;

                for (int sub = 0; sub < mesh.subMeshCount; sub++)
                {
                    Material mat = sub < mats.Length && mats[sub] != null
                        ? mats[sub]
                        : (mats.Length > 0 ? mats[0] : null);
                    if (mat == null) continue;

                    if (!groups.ContainsKey(mat))
                    {
                        groups[mat] = new List<CombineInstance>();
                        materialOrder.Add(mat);
                    }

                    groups[mat].Add(new CombineInstance
                    {
                        mesh = mesh,
                        subMeshIndex = sub,
                        transform = matrix
                    });
                }
            }

            if (materialOrder.Count == 0)
            {
                materials = Array.Empty<Material>();
                return null;
            }

            var finalCombines = new List<CombineInstance>();
            var tempMeshes = new List<Mesh>();

            foreach (var mat in materialOrder)
            {
                var groupMesh = new Mesh();
                groupMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                groupMesh.CombineMeshes(groups[mat].ToArray(), true, true);
                tempMeshes.Add(groupMesh);

                finalCombines.Add(new CombineInstance
                {
                    mesh = groupMesh,
                    transform = Matrix4x4.identity
                });
            }

            var combined = new Mesh();
            combined.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            combined.CombineMeshes(finalCombines.ToArray(), false, false);

            // Recalculate normals/tangents from geometry.  Note: this
            // averages normals at shared vertices, which softens hard edges
            // (e.g. cube corners).  This is acceptable for baked lighting
            // but may look slightly different from the originals under
            // real-time lighting on non-static geometry.
            combined.RecalculateNormals();
            combined.RecalculateTangents();
            combined.RecalculateBounds();

            // Generate non-overlapping UV2 for lightmapping
            var uvSettings = new UnwrapParam();
            UnwrapParam.SetDefaults(out uvSettings);
            uvSettings.packMargin = 0.02f;  // increase chart padding to avoid overlap warnings
            Unwrapping.GenerateSecondaryUVSet(combined, uvSettings);

            foreach (var tmp in tempMeshes)
                DestroyImmediate(tmp);

            materials = materialOrder.ToArray();
            return combined;
        }

        private static Transform FindParentOfShapeContainers(
            Transform parent, int depth, int maxDepth)
        {
            if (depth >= maxDepth) return null;

            for (int i = 0; i < parent.childCount; i++)
            {
                if (parent.GetChild(i).name.StartsWith(
                        ContainerPrefix, StringComparison.OrdinalIgnoreCase))
                    return parent;
            }

            for (int i = 0; i < parent.childCount; i++)
            {
                Transform found =
                    FindParentOfShapeContainers(parent.GetChild(i), depth + 1, maxDepth);
                if (found != null) return found;
            }

            return null;
        }

        private static string SanitizeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        private struct ShapeInfo
        {
            public Transform Transform;
            public MeshFilter MeshFilter;
            public MeshRenderer MeshRenderer;
        }

        /// <summary>
        /// Recursively removes MeshFilter and MeshRenderer from all descendants.
        /// </summary>
        private static void StripRenderers(Transform root)
        {
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                var mf = child.GetComponent<MeshFilter>();
                if (mf != null) DestroyImmediate(mf);
                var mr = child.GetComponent<MeshRenderer>();
                if (mr != null) DestroyImmediate(mr);
                StripRenderers(child);
            }
        }

        /// <summary>
        /// Recursively destroys any child GameObjects that have no components
        /// besides Transform and no children of their own. Works bottom-up so
        /// that a chain of empty nodes is fully cleaned.
        /// </summary>
        private static void PruneEmptyChildren(Transform root)
        {
            for (int i = root.childCount - 1; i >= 0; i--)
            {
                Transform child = root.GetChild(i);
                PruneEmptyChildren(child);

                if (child.childCount == 0 &&
                    child.GetComponents<Component>().Length <= 1)
                {
                    DestroyImmediate(child.gameObject);
                }
            }
        }
    }
}

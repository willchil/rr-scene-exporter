using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using RecRoom.Core.Studio;

namespace CompositeSceneGenerator
{
    public static class PrefabResolver
    {
        public static Dictionary<Guid, GameObject> BuildPrefabLookup(RecRoomBuiltInObjectData registry)
        {
            var lookup = new Dictionary<Guid, GameObject>();
            if (registry == null)
                return lookup;

            var so = new SerializedObject(registry);
            var builtInObjects = so.FindProperty("builtInObjects");
            if (builtInObjects == null || !builtInObjects.isArray)
            {
                Debug.LogWarning("[PrefabResolver] Could not find 'builtInObjects' array on registry.");
                return lookup;
            }

            int total = builtInObjects.arraySize;
            int unreadable = 0;

            for (int i = 0; i < total; i++)
            {
                var entry = builtInObjects.GetArrayElementAtIndex(i);
                var prefabProp = entry.FindPropertyRelative("prefab");
                if (prefabProp == null)
                    continue;

                // The registry's prefab field references the RecRoomBuiltInObject component
                // on each prefab, not the root GameObject.
                var refObj = prefabProp.objectReferenceValue;
                if (refObj == null)
                    continue;

                Component comp = refObj as Component;
                GameObject prefabObj;
                if (comp != null)
                {
                    prefabObj = comp.gameObject;
                }
                else
                {
                    prefabObj = refObj as GameObject;
                    if (prefabObj == null) continue;
                    comp = FindComponentWithProperty(prefabObj, "prefabId");
                    if (comp == null) continue;
                }

                // Read prefabId.bytes — serialized as a 32-char hex string
                var compSo = new SerializedObject(comp);
                var prefabIdProp = compSo.FindProperty("prefabId");
                if (prefabIdProp == null)
                    continue;

                var bytesProp = prefabIdProp.FindPropertyRelative("bytes");
                if (bytesProp == null)
                {
                    unreadable++;
                    continue;
                }

                if (i == 0)
                    Debug.Log($"[PrefabResolver] bytes: propertyType={bytesProp.propertyType}, type={bytesProp.type}, isArray={bytesProp.isArray}, arraySize={bytesProp.arraySize}");

                if (!bytesProp.isArray || bytesProp.arraySize != 16)
                {
                    unreadable++;
                    continue;
                }

                byte[] guidBytes = new byte[16];
                for (int b = 0; b < 16; b++)
                    guidBytes[b] = (byte)bytesProp.GetArrayElementAtIndex(b).intValue;

                Guid guid = new Guid(guidBytes);
                if (guid == Guid.Empty)
                {
                    unreadable++;
                    continue;
                }

                if (!lookup.ContainsKey(guid))
                    lookup[guid] = prefabObj;
            }

            if (unreadable > 0)
                Debug.LogWarning($"[PrefabResolver] {unreadable}/{total} entries had unreadable GUIDs.");
            Debug.Log($"[PrefabResolver] Built lookup with {lookup.Count} prefab entries from {total} registry entries.");
            return lookup;
        }

        public static Guid ByteStringToGuid(Google.Protobuf.ByteString bytes)
        {
            if (bytes == null || bytes.Length != 16)
                return Guid.Empty;
            return new Guid(bytes.ToByteArray());
        }

        /// <summary>
        /// Open a RecRoomObjects scene additively, walk every GameObject and:
        ///   - collect prefabId → prefab mappings into <paramref name="lookup"/>,
        ///   - collect uniqueId → world-space transform info into
        ///     <paramref name="sceneTransforms"/>.
        /// Then close the scene. Either output may be null to skip that pass.
        /// </summary>
        public static void AddStudioObjectPrefabs(
            Dictionary<Guid, GameObject> lookup,
            string scenePath,
            Dictionary<Guid, SceneObjectInfo> sceneTransforms = null)
        {
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            int added = 0;
            int transforms = 0;

            try
            {
                foreach (var rootGo in scene.GetRootGameObjects())
                {
                    foreach (var t in rootGo.GetComponentsInChildren<Transform>(true))
                    {
                        ProcessGameObject(t.gameObject, lookup, sceneTransforms, ref added, ref transforms);
                    }
                }
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }

            Debug.Log($"[PrefabResolver] Scanned {scenePath}: added {added} Studio Object prefabs, captured {transforms} scene transforms.");
        }

        private static void ProcessGameObject(
            GameObject go,
            Dictionary<Guid, GameObject> lookup,
            Dictionary<Guid, SceneObjectInfo> sceneTransforms,
            ref int added,
            ref int transforms)
        {
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null)
                    continue;

                var compSo = new SerializedObject(comp);
                var prefabIdProp = compSo.FindProperty("prefabId");
                var uniqueIdProp = compSo.FindProperty("uniqueId");
                var parentUniqueIdProp = compSo.FindProperty("parentUniqueId");
                if (prefabIdProp == null && uniqueIdProp == null)
                    continue;

                // Prefab mapping (only on components that also reference the prefab)
                if (lookup != null && prefabIdProp != null)
                {
                    var prefabRefProp = compSo.FindProperty("recRoomObjectPrefab");
                    if (prefabRefProp != null && prefabRefProp.objectReferenceValue != null)
                    {
                        GameObject prefabObj = null;
                        var prefabRef = prefabRefProp.objectReferenceValue;
                        if (prefabRef is Component prefabComp)
                            prefabObj = prefabComp.gameObject;
                        else if (prefabRef is GameObject g)
                            prefabObj = g;

                        if (prefabObj != null && TryReadGuid(prefabIdProp, out var prefabGuid)
                            && !lookup.ContainsKey(prefabGuid))
                        {
                            lookup[prefabGuid] = prefabObj;
                            added++;
                        }
                    }
                }

                // Transform mapping (keyed by uniqueId)
                if (sceneTransforms != null && uniqueIdProp != null
                    && TryReadGuid(uniqueIdProp, out var uniqueGuid)
                    && !sceneTransforms.ContainsKey(uniqueGuid))
                {
                    var tr = go.transform;

                    // Walk up the Unity transform hierarchy to find the nearest
                    // ancestor that itself has a uniqueId. parentUniqueId on the
                    // component sometimes skips raw container GameObjects (e.g.
                    // Replicator content roots), so we resolve the chain directly
                    // from the scene.
                    Guid parentGuid = FindNearestAncestorUniqueId(tr);

                    // Deformation (Rooms 2 equivalent of SandboxDeformationData).
                    // Only applied when deformationTransformState == 2 (active).
                    Vector3 deformation = Vector3.one;
                    var stateProp = compSo.FindProperty("deformationTransformState");
                    var deformScaleProp = compSo.FindProperty("deformationTransformLocalScale");
                    if (stateProp != null && deformScaleProp != null
                        && stateProp.intValue == 2)
                    {
                        deformation = deformScaleProp.vector3Value;
                    }

                    sceneTransforms[uniqueGuid] = new SceneObjectInfo
                    {
                        Position = tr.position,
                        Rotation = tr.rotation,
                        LossyScale = tr.lossyScale,
                        ParentId = parentGuid,
                        Deformation = deformation,
                        Name = go.name,
                    };
                    transforms++;
                }
            }
        }

        private static bool TryReadGuid(SerializedProperty prop, out Guid guid)
        {
            guid = Guid.Empty;
            var bytesProp = prop.FindPropertyRelative("bytes");
            if (bytesProp == null || !bytesProp.isArray || bytesProp.arraySize != 16)
                return false;

            byte[] guidBytes = new byte[16];
            for (int b = 0; b < 16; b++)
                guidBytes[b] = (byte)bytesProp.GetArrayElementAtIndex(b).intValue;

            guid = new Guid(guidBytes);
            return guid != Guid.Empty;
        }

        /// <summary>
        /// Walk up <paramref name="t"/>'s parents and return the uniqueId of the
        /// first ancestor that has one. Returns Guid.Empty if none found.
        /// </summary>
        private static Guid FindNearestAncestorUniqueId(Transform t)
        {
            for (var p = t.parent; p != null; p = p.parent)
            {
                foreach (var c in p.GetComponents<Component>())
                {
                    if (c == null) continue;
                    var pSo = new SerializedObject(c);
                    var uidProp = pSo.FindProperty("uniqueId");
                    if (uidProp != null && TryReadGuid(uidProp, out var g))
                        return g;
                }
            }
            return Guid.Empty;
        }

        private static Guid HexToGuid(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length != 32)
                return Guid.Empty;
            byte[] guidBytes = new byte[16];
            for (int i = 0; i < 16; i++)
                guidBytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return new Guid(guidBytes);
        }

        private static Component FindComponentWithProperty(GameObject go, string propertyName)
        {
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) continue;
                var cSo = new SerializedObject(c);
                if (cSo.FindProperty(propertyName) != null)
                    return c;
            }
            return null;
        }
    }
}

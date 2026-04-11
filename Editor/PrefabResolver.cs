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
        /// Open a RecRoomObjects scene additively, scan its registry entries for
        /// prefabId → prefab mappings, add them to the lookup, then close the scene.
        /// </summary>
        public static void AddStudioObjectPrefabs(Dictionary<Guid, GameObject> lookup, string scenePath)
        {
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            int added = 0;

            try
            {
                foreach (var rootGo in scene.GetRootGameObjects())
                {
                    foreach (var comp in rootGo.GetComponents<Component>())
                    {
                        if (comp == null)
                            continue;

                        var compSo = new SerializedObject(comp);
                        var prefabIdProp = compSo.FindProperty("prefabId");
                        var prefabRefProp = compSo.FindProperty("recRoomObjectPrefab");
                        if (prefabIdProp == null || prefabRefProp == null)
                            continue;

                        // Read the prefab reference
                        var prefabRef = prefabRefProp.objectReferenceValue;
                        if (prefabRef == null)
                            continue;

                        GameObject prefabObj = null;
                        if (prefabRef is Component prefabComp)
                            prefabObj = prefabComp.gameObject;
                        else if (prefabRef is GameObject go)
                            prefabObj = go;
                        if (prefabObj == null)
                            continue;

                        // Read the prefabId GUID
                        var bytesProp = prefabIdProp.FindPropertyRelative("bytes");
                        if (bytesProp == null || !bytesProp.isArray || bytesProp.arraySize != 16)
                            continue;

                        byte[] guidBytes = new byte[16];
                        for (int b = 0; b < 16; b++)
                            guidBytes[b] = (byte)bytesProp.GetArrayElementAtIndex(b).intValue;

                        Guid guid = new Guid(guidBytes);
                        if (guid == Guid.Empty)
                            continue;

                        if (!lookup.ContainsKey(guid))
                        {
                            lookup[guid] = prefabObj;
                            added++;
                        }
                    }
                }
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }

            Debug.Log($"[PrefabResolver] Added {added} Studio Object prefabs from {scenePath}.");
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

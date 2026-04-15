using System;
using System.Collections.Generic;
using UnityEngine;
using RecRoom.Protobuf;
using Debug = UnityEngine.Debug;

namespace CompositeSceneGenerator
{
    /// <summary>
    /// Post-processor for Rec Room light prefabs.
    /// Adds Unity Light components and configures them from persisted room data.
    /// </summary>
    internal class LightPostProcessor : IPrefabPostProcessor
    {
        // Rec Room light prefab GUIDs
        static readonly Guid DomeLightId      = new Guid("8be5d9e6-3cd4-4428-9b5f-ddce6a13e97c");
        static readonly Guid PointLightV1Id   = new Guid("0b0d7a08-bda7-4597-88bc-a0aa0eab29b8");
        static readonly Guid PointLightId     = new Guid("871ddc21-1ece-455b-b157-59dbd8453e8a");
        static readonly Guid SpotlightId      = new Guid("1ec28e5f-4aa6-4ccc-ac25-a8c9805fd197");
        static readonly Guid SpotlightV1Id    = new Guid("ddeb1eeb-a19f-4a88-aa86-1180e2e479e6");

        private static readonly HashSet<Guid> s_ids = new HashSet<Guid>
        {
            DomeLightId, PointLightV1Id, PointLightId, SpotlightId, SpotlightV1Id
        };

        public IReadOnlyCollection<Guid> HandledPrefabIds => s_ids;

        // Child transform name where the Unity Light component should be attached
        // so it inherits the correct orientation from the prefab hierarchy.
        static string GetLightChildName(Guid prefabGuid)
        {
            if (prefabGuid == PointLightId || prefabGuid == PointLightV1Id)
                return "PointLight";
            if (prefabGuid == SpotlightId || prefabGuid == SpotlightV1Id)
                return "Spotlight";
            if (prefabGuid == DomeLightId)
                return "Point Light";
            return null;
        }

        public void PreparePrefab(GameObject prefabRoot, Guid prefabGuid)
        {
            bool isSpot = prefabGuid == SpotlightId || prefabGuid == SpotlightV1Id || prefabGuid == DomeLightId;
            LightType lightType = isSpot ? LightType.Spot : LightType.Point;

            GameObject lightTarget = prefabRoot;
            string childName = GetLightChildName(prefabGuid);
            if (childName != null)
            {
                Transform root = prefabRoot.transform.Find("(Root)");
                Transform child = root != null ? root.Find(childName) : null;
                if (child != null)
                    lightTarget = child.gameObject;
            }

            var light = lightTarget.GetComponentInChildren<Light>();
            if (light == null)
                light = lightTarget.AddComponent<Light>();

            // Shared base configuration — inherited by every instance
            light.type = lightType;
            light.lightmapBakeType = LightmapBakeType.Mixed;
            light.shadows = LightShadows.Soft;
            light.enabled = true;
            light.range = 1f;
            light.intensity = 0.1f;
            if (isSpot)
                light.spotAngle = 60f;
        }

        public void Process(GameObject instance, Guid prefabGuid, PersistenceViewData view)
        {
            // Find the Light component added by PreparePrefab
            GameObject lightTarget = instance;
            string childName = GetLightChildName(prefabGuid);
            if (childName != null)
            {
                Transform root = instance.transform.Find("(Root)");
                Transform child = root != null ? root.Find(childName) : null;
                if (child != null)
                    lightTarget = child.gameObject;
                else
                    Debug.LogWarning($"[LightPostProcessor] Could not find child (Root)/{childName} on {instance.name}; attaching light to root.", instance);
            }

            var light = lightTarget.GetComponentInChildren<Light>();
            if (light == null)
            {
                Debug.LogWarning($"[LightPostProcessor] No Light component found on {instance.name}. Was PreparePrefab called?", instance);
                return;
            }

            // Per-instance overrides from room data
            bool isDomeLight = prefabGuid == DomeLightId;
            bool isSpot = prefabGuid == SpotlightId || prefabGuid == SpotlightV1Id || isDomeLight;
            var dld = view.DynamicLightData;
            if (dld != null)
            {
                light.enabled = dld.Emit;
                light.range = dld.Range > 0 ? (isDomeLight ? dld.Range : dld.Range / 10f) : 1f;
                light.intensity = dld.Intensity > 0 ? dld.Intensity / 10f : 0.1f;
            }

            if (isSpot)
            {
                float angle = 60f;
                if ((prefabGuid == SpotlightId || prefabGuid == SpotlightV1Id) && view.SpotlightData != null)
                    angle = view.SpotlightData.Angle;
                else if (isDomeLight && view.DomeLightData != null)
                    angle = view.DomeLightData.Angle;
                light.spotAngle = Mathf.Clamp(angle, 1f, 179f);
            }

            light.color = RecRoomColorUtility.DecodeColor(view.SandboxColorableData);
        }
    }
}

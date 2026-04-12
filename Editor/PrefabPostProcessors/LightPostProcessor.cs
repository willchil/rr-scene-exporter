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

        public void Process(GameObject instance, Guid prefabGuid, PersistenceViewData view)
        {
            bool isSpot = prefabGuid == SpotlightId || prefabGuid == SpotlightV1Id || prefabGuid == DomeLightId;
            LightType lightType = isSpot ? LightType.Spot : LightType.Point;

            var light = instance.GetComponentInChildren<Light>();
            if (light == null)
                light = instance.AddComponent<Light>();

            light.type = lightType;

            // Dynamic light data: intensity, range, emit, specular
            var dld = view.DynamicLightData;
            if (dld != null)
            {
                light.enabled = dld.Emit;
                light.range = dld.Range > 0 ? dld.Range / 10f : 1f;
                light.intensity = dld.Intensity > 0 ? dld.Intensity / 10f : 0.1f;
            }
            else
            {
                light.enabled = true;
                light.range = 1f;
                light.intensity = 0.1f;
            }

            // Spot angle: from SpotlightData or DomeLightData
            if (isSpot)
            {
                float angle = 60f; // default
                if (prefabGuid == SpotlightId && view.SpotlightData != null)
                    angle = view.SpotlightData.Angle;
                else if (prefabGuid == DomeLightId && view.DomeLightData != null)
                    angle = view.DomeLightData.Angle;

                // Clamp to Unity's valid range
                light.spotAngle = Mathf.Clamp(angle, 1f, 179f);
            }

            // Color: 32-bit int where low byte is palette index, upper 3 bytes are RGB
            light.color = RecRoomColorUtility.DecodeColor(view.SandboxColorableData);

            // Shadows disabled — too many additional lights overwhelm URP's shadow atlas
            light.shadows = LightShadows.None;
        }
    }
}

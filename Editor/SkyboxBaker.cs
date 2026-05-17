using System.IO;
using UnityEditor;
using UnityEngine;

namespace CompositeSceneGenerator
{
    /// <summary>
    /// Bakes the active scene's skybox material into a portable cubemap and a
    /// standard `Skybox/Cubemap` material so the composite scene no longer
    /// depends on the source skybox shader/textures.
    /// </summary>
    internal static class SkyboxBaker
    {
        /// <summary>
        /// Render <paramref name="sourceSkybox"/> into a cubemap and produce a
        /// new material referencing it. The cubemap and material are saved as
        /// assets next to the composite scene. Returns the new material, or
        /// null if there was nothing to bake.
        /// </summary>
        public static Material Bake(Material sourceSkybox, string scenePath, int faceSize = 512)
        {
            if (sourceSkybox == null || string.IsNullOrEmpty(scenePath))
                return null;

            string assetDir = Path.GetDirectoryName(scenePath).Replace('\\', '/');
            string baseName = Path.GetFileNameWithoutExtension(scenePath);
            string cubePath = $"{assetDir}/{baseName}_Skybox.cubemap";
            string matPath = $"{assetDir}/{baseName}_Skybox.mat";

            // RenderToCubemap reads RenderSettings.skybox; make sure the source
            // is set during the bake regardless of any prior state.
            var prevSkybox = RenderSettings.skybox;
            RenderSettings.skybox = sourceSkybox;
            DynamicGI.UpdateEnvironment();

            Cubemap cubemap;
            var camGo = new GameObject("__SkyboxBakeCamera__")
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            try
            {
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.Skybox;
                cam.cullingMask = 0;
                cam.enabled = false;
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = 1000f;
                camGo.transform.position = Vector3.zero;
                camGo.transform.rotation = Quaternion.identity;

                cubemap = new Cubemap(faceSize, TextureFormat.RGBA32, mipChain: true);
                if (!cam.RenderToCubemap(cubemap))
                {
                    Debug.LogWarning("[SkyboxBaker] Camera.RenderToCubemap returned false; skipping skybox bake.");
                    Object.DestroyImmediate(cubemap);
                    RenderSettings.skybox = prevSkybox;
                    return null;
                }
                cubemap.Apply(updateMipmaps: true, makeNoLongerReadable: false);
            }
            finally
            {
                Object.DestroyImmediate(camGo);
            }

            Directory.CreateDirectory(assetDir);

            // Replace any prior bake at the same path so re-runs don't pile up.
            AssetDatabase.DeleteAsset(cubePath);
            AssetDatabase.DeleteAsset(matPath);

            AssetDatabase.CreateAsset(cubemap, cubePath);

            var skyShader = Shader.Find("Skybox/Cubemap");
            if (skyShader == null)
            {
                Debug.LogWarning("[SkyboxBaker] 'Skybox/Cubemap' shader not found; skipping material creation.");
                RenderSettings.skybox = prevSkybox;
                return null;
            }

            var mat = new Material(skyShader);
            mat.SetTexture("_Tex", cubemap);
            AssetDatabase.CreateAsset(mat, matPath);
            AssetDatabase.SaveAssets();

            Debug.Log($"[SkyboxBaker] Baked skybox '{sourceSkybox.name}' → {cubePath}");
            return mat;
        }
    }
}

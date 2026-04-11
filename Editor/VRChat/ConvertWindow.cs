using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace RRSceneExporter.VRChat
{
    public static class ConvertWindow
    {
        [MenuItem("Rec Room/Convert materials")]
        private static void Convert()
        {
            Scene scene = EditorSceneManager.GetActiveScene();
            string sceneName = Path.GetFileNameWithoutExtension(scene.path);
            if (string.IsNullOrEmpty(sceneName))
            {
                EditorUtility.DisplayDialog("Convert", "Save the scene before converting.", "OK");
                return;
            }

            string logPath = "Assets/RecRoomCache/ShaderLog/" + sceneName + ".json";
            if (!File.Exists(Path.GetFullPath(logPath)))
            {
                EditorUtility.DisplayDialog("Convert",
                    $"Shader log not found at:\n{logPath}\n\nExport the scene from Rec Room Studio first.", "OK");
                return;
            }

            string json = File.ReadAllText(Path.GetFullPath(logPath));
            var log = JsonUtility.FromJson<ShaderLog>(json);
            if (log == null || log.materials == null || log.materials.Count == 0)
            {
                Debug.Log("[RRConvert] Shader log is empty. Nothing to convert.");
                return;
            }

            var shaderMap = BuildShaderMap();
            Shader standardShader = Shader.Find("Standard");
            Shader unlitShader = Shader.Find("Unlit/Texture");

            if (standardShader == null || unlitShader == null)
            {
                Debug.LogError("[RRConvert] Could not find Standard or Unlit/Texture shaders.");
                return;
            }

            int converted = 0;
            foreach (var entry in log.materials)
            {
                var mat = AssetDatabase.LoadAssetAtPath<Material>(entry.materialPath);
                if (mat == null)
                    continue;

                if (!shaderMap.TryGetValue(entry.shaderName, out ShaderMapping mapping))
                {
                    Debug.LogWarning($"[RRConvert] No mapping for shader '{entry.shaderName}' on {entry.materialPath}");
                    continue;
                }

                // Read saved properties via SerializedObject — the Material API
                // returns defaults when the shader is missing (InternalErrorShader),
                // but the serialized data still has the original values.
                var so = new UnityEditor.SerializedObject(mat);
                Color baseColor = GetSavedColor(so, "_BaseColor", Color.white);
                Texture baseMap = GetSavedTexture(so, "_BaseMap");
                Texture bumpMap = GetSavedTexture(so, "_BumpMap");
                float bumpScale = GetSavedFloat(so, "_BumpScale", 1f);
                float smoothness = GetSavedFloat(so, "_Smoothness", 0.5f);
                float metallic = GetSavedFloat(so, "_Metallic", 0f);
                Color emissionColor = GetSavedColor(so, "_EmissionColor", Color.black);
                Texture emissionMap = GetSavedTexture(so, "_EmissionMap");

                // Assign Built-in shader
                if (mapping.Category == ShaderCategory.Lit)
                {
                    mat.shader = standardShader;

                    // Standard shader property names
                    mat.SetColor("_Color", baseColor);
                    if (baseMap != null) mat.SetTexture("_MainTex", baseMap);
                    if (bumpMap != null)
                    {
                        mat.SetTexture("_BumpMap", bumpMap);
                        mat.SetFloat("_BumpScale", bumpScale);
                        mat.EnableKeyword("_NORMALMAP");
                    }
                    mat.SetFloat("_Glossiness", smoothness);
                    mat.SetFloat("_Metallic", metallic);

                    // Emission
                    bool hasEmission = emissionColor.r > 0 || emissionColor.g > 0 || emissionColor.b > 0;
                    if (hasEmission || emissionMap != null)
                    {
                        mat.EnableKeyword("_EMISSION");
                        mat.SetColor("_EmissionColor", emissionColor);
                        if (emissionMap != null)
                            mat.SetTexture("_EmissionMap", emissionMap);
                        mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                    }

                    // Surface type
                    if (mapping.Transparent)
                    {
                        mat.SetFloat("_Mode", 3f); // Standard shader: Transparent
                        mat.SetInt("_SrcBlend", (int)BlendMode.One);
                        mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                        mat.SetInt("_ZWrite", 0);
                        mat.DisableKeyword("_ALPHATEST_ON");
                        mat.DisableKeyword("_ALPHABLEND_ON");
                        mat.EnableKeyword("_ALPHAPREMULTIPLY_ON");
                        mat.renderQueue = (int)RenderQueue.Transparent;
                    }
                    else if (mapping.AlphaClip)
                    {
                        mat.SetFloat("_Mode", 1f); // Standard shader: Cutout
                        mat.SetInt("_SrcBlend", (int)BlendMode.One);
                        mat.SetInt("_DstBlend", (int)BlendMode.Zero);
                        mat.SetInt("_ZWrite", 1);
                        mat.EnableKeyword("_ALPHATEST_ON");
                        mat.DisableKeyword("_ALPHABLEND_ON");
                        mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                        mat.renderQueue = (int)RenderQueue.AlphaTest;
                    }
                }
                else // Unlit
                {
                    mat.shader = unlitShader;
                    if (baseMap != null)
                        mat.SetTexture("_MainTex", baseMap);
                    // Unlit/Texture doesn't support color tinting;
                    // for transparency/additive we'd need a different shader
                }

                EditorUtility.SetDirty(mat);
                converted++;
            }

            if (converted > 0)
                AssetDatabase.SaveAssets();

            Debug.Log($"[RRConvert] Converted {converted}/{log.materials.Count} materials to Built-in pipeline.");
            EditorUtility.DisplayDialog("Convert",
                $"Converted {converted} of {log.materials.Count} materials to Built-in pipeline.", "OK");
        }

        // ── SerializedObject helpers for reading properties from broken-shader materials ──

        private static Color GetSavedColor(SerializedObject so, string name, Color fallback)
        {
            var colors = so.FindProperty("m_SavedProperties.m_Colors");
            if (colors == null) return fallback;
            for (int i = 0; i < colors.arraySize; i++)
            {
                var pair = colors.GetArrayElementAtIndex(i);
                if (pair.FindPropertyRelative("first").stringValue == name)
                    return pair.FindPropertyRelative("second").colorValue;
            }
            return fallback;
        }

        private static float GetSavedFloat(SerializedObject so, string name, float fallback)
        {
            var floats = so.FindProperty("m_SavedProperties.m_Floats");
            if (floats == null) return fallback;
            for (int i = 0; i < floats.arraySize; i++)
            {
                var pair = floats.GetArrayElementAtIndex(i);
                if (pair.FindPropertyRelative("first").stringValue == name)
                    return pair.FindPropertyRelative("second").floatValue;
            }
            return fallback;
        }

        private static Texture GetSavedTexture(SerializedObject so, string name)
        {
            var texEnvs = so.FindProperty("m_SavedProperties.m_TexEnvs");
            if (texEnvs == null) return null;
            for (int i = 0; i < texEnvs.arraySize; i++)
            {
                var pair = texEnvs.GetArrayElementAtIndex(i);
                if (pair.FindPropertyRelative("first").stringValue == name)
                    return pair.FindPropertyRelative("second.m_Texture").objectReferenceValue as Texture;
            }
            return null;
        }

        // ── Shared types (mirror of RRS-side) ──────────────────────────────

        private enum ShaderCategory { Lit, Unlit }

        private struct ShaderMapping
        {
            public ShaderCategory Category;
            public bool Transparent;
            public bool AlphaClip;
            public bool Additive;
            public Dictionary<string, string> PropertyRenames;
        }

        [Serializable]
        private class ShaderLogEntry
        {
            public string materialPath;
            public string shaderName;
        }

        [Serializable]
        private class ShaderLog
        {
            public List<ShaderLogEntry> materials = new List<ShaderLogEntry>();
        }

        // ── Shader map (same entries as RRS side) ──────────────────────────

        private static Dictionary<string, ShaderMapping> BuildShaderMap()
        {
            var map = new Dictionary<string, ShaderMapping>(StringComparer.OrdinalIgnoreCase);

            var litOpaque = new ShaderMapping { Category = ShaderCategory.Lit };
            map["Hidden/Rec Room Stub/AG/Standard-Opaque"] = litOpaque;
            map["Hidden/Rec Room Stub/AG/Standard-Opaque-Instanced"] = litOpaque;
            map["Hidden/Rec Room Stub/AG/Standard-Opaque-InstancedST"] = litOpaque;
            map["Hidden/Rec Room Stub/AG/Standard-Opaque-Projection"] = litOpaque;
            map["Hidden/Rec Room Stub/AG/Standard-Opaque-Decal"] = litOpaque;

            var litOpaqueEmission = new ShaderMapping { Category = ShaderCategory.Lit };
            map["Hidden/Rec Room Stub/AG/Standard-Opaque-Emission"] = litOpaqueEmission;
            map["Hidden/Rec Room Stub/AG/Standard-Opaque-Emission-Scroll"] = litOpaqueEmission;
            map["Hidden/Rec Room Stub/AG/Standard-Opaque-Emission-ScrollingMarquee"] = litOpaqueEmission;
            map["Hidden/Rec Room Stub/AG/Standard-Opaque-TwoEmissionChannels"] = litOpaqueEmission;

            var litAlphaClip = new ShaderMapping { Category = ShaderCategory.Lit, AlphaClip = true };
            map["Hidden/Rec Room Stub/AG/Standard-Opaque-AlphaClip"] = litAlphaClip;

            var litEmissionClip = new ShaderMapping { Category = ShaderCategory.Lit, AlphaClip = true };
            map["Hidden/Rec Room Stub/AG/Standard-Opaque-Emission-AlphaClip"] = litEmissionClip;
            map["Hidden/Rec Room Stub/AG/Standard-Opaque-Emission-AlphaClip-Sine"] = litEmissionClip;
            map["Hidden/Rec Room Stub/AG/UI-Standard-Opaque-Emission-AlphaClip"] = litEmissionClip;

            var litTransparent = new ShaderMapping { Category = ShaderCategory.Lit, Transparent = true };
            map["Hidden/Rec Room Stub/AG/Standard-Transparent"] = litTransparent;
            map["Hidden/Rec Room Stub/AG/Standard-Transparent-GlassPane"] = litTransparent;

            var litTransparentEmission = new ShaderMapping { Category = ShaderCategory.Lit, Transparent = true };
            map["Hidden/Rec Room Stub/AG/Standard-Transparent-Emission"] = litTransparentEmission;
            map["Hidden/Rec Room Stub/AG/Standard-Transparent-Emission-Sine"] = litTransparentEmission;

            var unlitColor = new ShaderMapping
            {
                Category = ShaderCategory.Unlit,
                PropertyRenames = new Dictionary<string, string> { { "_InstanceColor", "_BaseColor" } }
            };
            map["Hidden/Rec Room Stub/AG/Unlit-Color"] = unlitColor;

            var unlitTexture = new ShaderMapping
            {
                Category = ShaderCategory.Unlit,
                PropertyRenames = new Dictionary<string, string> { { "_InstanceColor", "_BaseColor" } }
            };
            map["Hidden/Rec Room Stub/AG/Unlit-Texture"] = unlitTexture;
            map["Hidden/Rec Room Stub/AG/UI-Unlit-Texture"] = unlitTexture;

            var unlitTransparent = new ShaderMapping { Category = ShaderCategory.Unlit, Transparent = true };
            map["Hidden/Rec Room Stub/AG/Unlit-Transparent"] = unlitTransparent;
            map["Hidden/Rec Room Stub/AG/Unlit-Transparent-Instanced"] = unlitTransparent;

            var unlitAdditive = new ShaderMapping { Category = ShaderCategory.Unlit, Transparent = true, Additive = true };
            map["Hidden/Rec Room Stub/AG/Unlit-Additive"] = unlitAdditive;
            map["Hidden/Rec Room Stub/AG/Unlit-Additive-Color"] = unlitAdditive;
            map["Hidden/Rec Room Stub/AG/Unlit-Additive-Soft"] = unlitAdditive;
            map["Hidden/Rec Room Stub/AG/UI-Unlit-Additive-Soft"] = unlitAdditive;

            var litFoliage = new ShaderMapping { Category = ShaderCategory.Lit, AlphaClip = true };
            map["Rec Room Studio/Foliage"] = litFoliage;

            var litWater = new ShaderMapping { Category = ShaderCategory.Lit, Transparent = true };
            map["Rec Room Studio/Water"] = litWater;
            map["Hidden/Rec Room Stub/AG/Water"] = litWater;
            map["Hidden/Rec Room Stub/AG/StudioWater"] = litWater;

            var unlitHolographic = new ShaderMapping
            {
                Category = ShaderCategory.Unlit, Transparent = true,
                PropertyRenames = new Dictionary<string, string> { { "_MainColor", "_BaseColor" } }
            };
            map["Rec Room Studio/Holographic"] = unlitHolographic;

            var litAvatar = new ShaderMapping { Category = ShaderCategory.Lit };
            map["Rec Room Studio/Avatar"] = litAvatar;
            map["Hidden/Rec Room Studio/Avatar Face"] = litAvatar;
            map["Hidden/Rec Room Stub/AG/Avatar-Batched"] = litAvatar;
            map["Hidden/Rec Room Stub/AG/Avatar-Decal"] = litAvatar;
            map["Hidden/Rec Room Stub/AG/Avatar-Emission"] = litAvatar;
            map["Hidden/Rec Room Stub/AG/Ignore Effect"] = litAvatar;

            var unlitParticle = new ShaderMapping { Category = ShaderCategory.Unlit, Transparent = true };
            map["Hidden/Rec Room Stub/Shader Forge/Particles/AlphaBlended"] = unlitParticle;
            map["Hidden/Rec Room Stub/Shader Forge/Particles/FogSin"] = unlitParticle;
            map["Hidden/Rec Room Stub/Shader Forge/Particles/MultiplyDecal"] = unlitParticle;
            map["Hidden/Rec Room Stub/Shader Forge/Particles/PaperFlame"] = unlitParticle;
            map["Hidden/Rec Room Stub/Shader Forge/Particles/PortalFog"] = unlitParticle;
            map["Hidden/Rec Room Stub/Shader Forge/Particles/PortalFogFresnel"] = unlitParticle;

            var unlitParticleAdditive = new ShaderMapping { Category = ShaderCategory.Unlit, Transparent = true, Additive = true };
            map["Hidden/Rec Room Stub/Shader Forge/Particles/AdditiveAlphaClip"] = unlitParticleAdditive;
            map["Hidden/Rec Room Stub/Shader Forge/Particles/AdditiveFadeDistance"] = unlitParticleAdditive;
            map["Hidden/Rec Room Stub/Shader Forge/Confetti"] = unlitParticleAdditive;

            var litLeaf = new ShaderMapping { Category = ShaderCategory.Lit, AlphaClip = true };
            map["Hidden/Rec Room Stub/Nature/Leaf"] = litLeaf;
            map["Hidden/Rec Room Stub/Nature/Leaf_NoViewClip"] = litLeaf;

            // Also map the URP shaders that the RRS-side may have already remapped to
            map["Universal Render Pipeline/Lit"] = litOpaque;
            map["Rec Room Studio/Lit"] = litOpaque;
            map["Universal Render Pipeline/Unlit"] = unlitColor;
            map["Rec Room Studio/Unlit"] = unlitColor;

            return map;
        }
    }
}

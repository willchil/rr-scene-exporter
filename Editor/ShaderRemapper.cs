using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace CompositeSceneGenerator
{
    internal enum ShaderCategory { Lit, Unlit }

    internal struct ShaderMapping
    {
        public ShaderCategory Category;
        public bool Transparent;
        public bool AlphaClip;
        public bool Additive;
        public Dictionary<string, string> PropertyRenames;
    }

    [Serializable]
    internal class ShaderLogEntry
    {
        public string materialPath;
        public string shaderName;
    }

    [Serializable]
    internal class ShaderLog
    {
        public List<ShaderLogEntry> materials = new List<ShaderLogEntry>();
    }

    internal static class ShaderRemapper
    {
        private static readonly Dictionary<string, ShaderMapping> s_shaderMap = BuildShaderMap();

        internal static Dictionary<string, ShaderMapping> BuildShaderMap()
        {
            var map = new Dictionary<string, ShaderMapping>(StringComparer.OrdinalIgnoreCase);

            // ─ AG Standard (Lit, opaque) ─
            var litOpaque = new ShaderMapping { Category = ShaderCategory.Lit };
            map["Hidden/Rec Room Stub/AG/Standard-Opaque"] = litOpaque;
            map["Hidden/Rec Room Stub/AG/Standard-Opaque-Instanced"] = litOpaque;
            map["Hidden/Rec Room Stub/AG/Standard-Opaque-InstancedST"] = litOpaque;
            map["Hidden/Rec Room Stub/AG/Standard-Opaque-Projection"] = litOpaque;
            map["Hidden/Rec Room Stub/AG/Standard-Opaque-Decal"] = litOpaque;

            // ─ AG Standard (Lit, opaque, emission) ─
            var litOpaqueEmission = new ShaderMapping { Category = ShaderCategory.Lit };
            map["Hidden/Rec Room Stub/AG/Standard-Opaque-Emission"] = litOpaqueEmission;
            map["Hidden/Rec Room Stub/AG/Standard-Opaque-Emission-Scroll"] = litOpaqueEmission;
            map["Hidden/Rec Room Stub/AG/Standard-Opaque-Emission-ScrollingMarquee"] = litOpaqueEmission;
            map["Hidden/Rec Room Stub/AG/Standard-Opaque-TwoEmissionChannels"] = litOpaqueEmission;

            // ─ AG Standard (Lit, opaque, alpha clip) ─
            var litAlphaClip = new ShaderMapping { Category = ShaderCategory.Lit, AlphaClip = true };
            map["Hidden/Rec Room Stub/AG/Standard-Opaque-AlphaClip"] = litAlphaClip;

            // ─ AG Standard (Lit, opaque, emission + alpha clip) ─
            var litEmissionClip = new ShaderMapping { Category = ShaderCategory.Lit, AlphaClip = true };
            map["Hidden/Rec Room Stub/AG/Standard-Opaque-Emission-AlphaClip"] = litEmissionClip;
            map["Hidden/Rec Room Stub/AG/Standard-Opaque-Emission-AlphaClip-Sine"] = litEmissionClip;
            map["Hidden/Rec Room Stub/AG/UI-Standard-Opaque-Emission-AlphaClip"] = litEmissionClip;

            // ─ AG Standard (Lit, transparent) ─
            var litTransparent = new ShaderMapping { Category = ShaderCategory.Lit, Transparent = true };
            map["Hidden/Rec Room Stub/AG/Standard-Transparent"] = litTransparent;
            map["Hidden/Rec Room Stub/AG/Standard-Transparent-GlassPane"] = litTransparent;

            // ─ AG Standard (Lit, transparent, emission) ─
            var litTransparentEmission = new ShaderMapping { Category = ShaderCategory.Lit, Transparent = true };
            map["Hidden/Rec Room Stub/AG/Standard-Transparent-Emission"] = litTransparentEmission;
            map["Hidden/Rec Room Stub/AG/Standard-Transparent-Emission-Sine"] = litTransparentEmission;

            // ─ AG Unlit (opaque, color only) ─
            var unlitColor = new ShaderMapping
            {
                Category = ShaderCategory.Unlit,
                PropertyRenames = new Dictionary<string, string> { { "_InstanceColor", "_BaseColor" } }
            };
            map["Hidden/Rec Room Stub/AG/Unlit-Color"] = unlitColor;

            // ─ AG Unlit (opaque, textured) ─
            var unlitTexture = new ShaderMapping
            {
                Category = ShaderCategory.Unlit,
                PropertyRenames = new Dictionary<string, string> { { "_InstanceColor", "_BaseColor" } }
            };
            map["Hidden/Rec Room Stub/AG/Unlit-Texture"] = unlitTexture;
            map["Hidden/Rec Room Stub/AG/UI-Unlit-Texture"] = unlitTexture;

            // ─ AG Unlit (transparent) ─
            var unlitTransparent = new ShaderMapping { Category = ShaderCategory.Unlit, Transparent = true };
            map["Hidden/Rec Room Stub/AG/Unlit-Transparent"] = unlitTransparent;
            map["Hidden/Rec Room Stub/AG/Unlit-Transparent-Instanced"] = unlitTransparent;

            // ─ AG Unlit (additive) ─
            var unlitAdditive = new ShaderMapping { Category = ShaderCategory.Unlit, Transparent = true, Additive = true };
            map["Hidden/Rec Room Stub/AG/Unlit-Additive"] = unlitAdditive;
            map["Hidden/Rec Room Stub/AG/Unlit-Additive-Color"] = unlitAdditive;
            map["Hidden/Rec Room Stub/AG/Unlit-Additive-Soft"] = unlitAdditive;
            map["Hidden/Rec Room Stub/AG/UI-Unlit-Additive-Soft"] = unlitAdditive;

            // ─ Rec Room Studio shaders ─
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

            // ─ Avatar shaders → Lit ─
            var litAvatar = new ShaderMapping { Category = ShaderCategory.Lit };
            map["Rec Room Studio/Avatar"] = litAvatar;
            map["Hidden/Rec Room Studio/Avatar Face"] = litAvatar;
            map["Hidden/Rec Room Stub/AG/Avatar-Batched"] = litAvatar;
            map["Hidden/Rec Room Stub/AG/Avatar-Decal"] = litAvatar;
            map["Hidden/Rec Room Stub/AG/Avatar-Emission"] = litAvatar;
            map["Hidden/Rec Room Stub/AG/Ignore Effect"] = litAvatar;

            // ─ Particles → URP Unlit ─
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

            // ─ Nature ─
            var litLeaf = new ShaderMapping { Category = ShaderCategory.Lit, AlphaClip = true };
            map["Hidden/Rec Room Stub/Nature/Leaf"] = litLeaf;
            map["Hidden/Rec Room Stub/Nature/Leaf_NoViewClip"] = litLeaf;

            return map;
        }

        internal static void RemapRecRoomShaders(Dictionary<string, string> pathMap)
        {
            Shader urpLit = Shader.Find("Universal Render Pipeline/Lit")
                         ?? Shader.Find("Rec Room Studio/Lit");
            Shader urpUnlit = Shader.Find("Universal Render Pipeline/Unlit")
                           ?? Shader.Find("Rec Room Studio/Unlit");

            if (urpLit == null || urpUnlit == null)
            {
                Debug.LogWarning("[CompositeScene] Could not find URP Lit/Unlit shaders. Skipping shader remapping.");
                return;
            }

            int remapped = 0;
            foreach (var entry in pathMap)
            {
                string cachedPath = entry.Value;
                if (!cachedPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                    continue;

                var mat = AssetDatabase.LoadAssetAtPath<Material>(cachedPath);
                if (mat == null || mat.shader == null)
                    continue;

                string shaderName = mat.shader.name;
                if (!s_shaderMap.TryGetValue(shaderName, out ShaderMapping mapping))
                    continue;

                // Save property values that need renaming before shader swap
                var savedProps = new Dictionary<string, object>();
                if (mapping.PropertyRenames != null)
                {
                    foreach (var rename in mapping.PropertyRenames)
                    {
                        if (mat.HasProperty(rename.Key))
                        {
                            var type = mat.shader.GetPropertyType(mat.shader.FindPropertyIndex(rename.Key));
                            switch (type)
                            {
                                case ShaderPropertyType.Color:
                                    savedProps[rename.Value] = mat.GetColor(rename.Key);
                                    break;
                                case ShaderPropertyType.Float:
                                case ShaderPropertyType.Range:
                                    savedProps[rename.Value] = mat.GetFloat(rename.Key);
                                    break;
                                case ShaderPropertyType.Vector:
                                    savedProps[rename.Value] = mat.GetVector(rename.Key);
                                    break;
                                case ShaderPropertyType.Texture:
                                    savedProps[rename.Value] = mat.GetTexture(rename.Key);
                                    break;
                            }
                        }
                    }
                }

                Shader targetShader = mapping.Category == ShaderCategory.Lit ? urpLit : urpUnlit;
                mat.shader = targetShader;

                foreach (var prop in savedProps)
                {
                    if (prop.Value is Color c) mat.SetColor(prop.Key, c);
                    else if (prop.Value is float f) mat.SetFloat(prop.Key, f);
                    else if (prop.Value is Vector4 v) mat.SetVector(prop.Key, v);
                    else if (prop.Value is Texture t) mat.SetTexture(prop.Key, t);
                }

                if (mapping.Transparent)
                {
                    mat.SetFloat("_Surface", 1f);
                    mat.SetFloat("_ZWrite", 0f);
                    if (mapping.Additive)
                    {
                        mat.SetFloat("_Blend", 1f);
                        mat.SetFloat("_SrcBlend", (float)BlendMode.One);
                        mat.SetFloat("_DstBlend", (float)BlendMode.One);
                    }
                    else
                    {
                        mat.SetFloat("_Blend", 0f);
                        mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                        mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                    }
                    mat.renderQueue = (int)RenderQueue.Transparent;
                }

                if (mapping.AlphaClip)
                {
                    mat.SetFloat("_AlphaClip", 1f);
                    mat.EnableKeyword("_ALPHATEST_ON");
                    if (!mapping.Transparent)
                        mat.renderQueue = (int)RenderQueue.AlphaTest;
                }

                if (mat.HasProperty("_EmissionColor"))
                {
                    Color emission = mat.GetColor("_EmissionColor");
                    if (emission.r > 0 || emission.g > 0 || emission.b > 0)
                        mat.EnableKeyword("_EMISSION");
                }

                EditorUtility.SetDirty(mat);
                remapped++;
            }

            if (remapped > 0)
            {
                AssetDatabase.SaveAssets();
                Debug.Log($"[CompositeScene] Remapped {remapped} materials from Rec Room shaders to URP shaders.");
            }
        }

        internal static void WriteShaderLog(string scenePath)
        {
            var log = new ShaderLog();
            string[] deps = AssetDatabase.GetDependencies(scenePath, true);
            foreach (string dep in deps)
            {
                if (!dep.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                    continue;

                var mat = AssetDatabase.LoadAssetAtPath<Material>(dep);
                if (mat == null || mat.shader == null)
                    continue;

                string shaderName = mat.shader.name;
                if (shaderName == "Hidden/InternalErrorShader")
                    continue;

                log.materials.Add(new ShaderLogEntry
                {
                    materialPath = dep,
                    shaderName = shaderName
                });
            }

            string sceneName = Path.GetFileNameWithoutExtension(
                EditorSceneManager.GetActiveScene().path);
            if (string.IsNullOrEmpty(sceneName))
                sceneName = "Untitled";

            string logDir = "Assets/RecRoomCache/ShaderLog";
            AssetUtility.EnsureAssetFolderExists(logDir);

            string logPath = logDir + "/" + sceneName + ".json";
            File.WriteAllText(
                Path.GetFullPath(logPath),
                JsonUtility.ToJson(log, true));
            AssetDatabase.ImportAsset(logPath);
            Debug.Log($"[CompositeScene] Wrote shader log for {log.materials.Count} materials to {logPath}");
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace CompositeSceneGenerator
{
    internal static class GlbConverter
    {
        internal static GameObject ConvertAndImportGlb(
            DefaultAsset glbAsset, string blenderPath, string sceneName)
        {
            string glbAssetPath = AssetDatabase.GetAssetPath(glbAsset);
            string glbFullPath = Path.GetFullPath(glbAssetPath);
            string glbName = Path.GetFileNameWithoutExtension(glbFullPath);

            // If the GLB is generically named "Scene", use the scene name instead
            string cacheName = string.Equals(glbName, "Scene", StringComparison.OrdinalIgnoreCase)
                ? sceneName : glbName;

            string fbxAssetPath = $"Assets/RecRoomCache/MakerPen/{cacheName}.fbx";
            string fbxFullPath = Path.GetFullPath(fbxAssetPath);

            // Reuse existing cached FBX if available
            var existingPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(fbxAssetPath);
            if (existingPrefab != null)
            {
                Debug.Log($"[CompositeScene] Reusing cached FBX: {fbxAssetPath}");
                return existingPrefab;
            }

            if (!BlenderConverter.ConvertGlbToFbx(blenderPath, glbFullPath, fbxFullPath))
            {
                Debug.LogError("[CompositeScene] Blender conversion failed.");
                return null;
            }

            // Import textures first and mark normal maps before the FBX sees them
            string texDir = $"Assets/RecRoomCache/MakerPen/{cacheName}_Textures";
            AssetDatabase.ImportAsset(texDir, ImportAssetOptions.ImportRecursive | ImportAssetOptions.ForceSynchronousImport);
            MarkNormalMapTextures(texDir);

            AssetDatabase.ImportAsset(fbxAssetPath, ImportAssetOptions.ForceSynchronousImport);

            var importer = AssetImporter.GetAtPath(fbxAssetPath) as ModelImporter;
            if (importer != null)
            {
                importer.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;

                string matDir = $"Assets/RecRoomCache/MakerPen/{cacheName}_Materials";
                if (!Directory.Exists(Path.GetFullPath(matDir)))
                    Directory.CreateDirectory(Path.GetFullPath(matDir));
                AssetDatabase.Refresh();

                importer.SaveAndReimport();

                var materials = AssetDatabase.LoadAllAssetsAtPath(fbxAssetPath);
                foreach (var asset in materials)
                {
                    if (asset is Material mat && !AssetDatabase.IsMainAsset(asset))
                    {
                        string matPath = $"{matDir}/{mat.name}.mat";
                        if (File.Exists(Path.GetFullPath(matPath)))
                            continue;

                        string err = AssetDatabase.ExtractAsset(asset, matPath);
                        if (!string.IsNullOrEmpty(err))
                            Debug.LogWarning($"[CompositeScene] Failed to extract material {mat.name}: {err}");
                    }
                }

                AssetDatabase.Refresh();
                importer.SaveAndReimport();

                PatchMaterialTints(glbFullPath, matDir);
            }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(fbxAssetPath);
            if (prefab == null)
                Debug.LogError($"[CompositeScene] Failed to load imported FBX: {fbxAssetPath}");
            else
                Debug.Log($"[CompositeScene] Imported maker pen model: {fbxAssetPath}");

            return prefab;
        }

        private static void MarkNormalMapTextures(string texAssetDir)
        {
            string fullDir = Path.GetFullPath(texAssetDir);
            if (!Directory.Exists(fullDir))
                return;

            string[] extensions = { "*.png", "*.jpg", "*.tga" };
            int marked = 0;
            foreach (string ext in extensions)
            {
                foreach (string file in Directory.GetFiles(fullDir, ext))
                {
                    string fileName = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                    if (!fileName.Contains("norm") && !fileName.Contains("normal"))
                        continue;

                    string assetPath = texAssetDir + "/" + Path.GetFileName(file);
                    var texImporter = AssetImporter.GetAtPath(assetPath) as TextureImporter;
                    if (texImporter != null && texImporter.textureType != TextureImporterType.NormalMap)
                    {
                        texImporter.textureType = TextureImporterType.NormalMap;
                        texImporter.SaveAndReimport();
                        marked++;
                    }
                }
            }

            if (marked > 0)
                Debug.Log($"[CompositeScene] Marked {marked} textures as normal maps.");
        }

        private static void PatchMaterialTints(string glbPath, string matAssetDir)
        {
            Dictionary<string, float[]> matColors;
            try
            {
                matColors = ReadGlbMaterialColors(glbPath);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CompositeScene] Failed to read GLB material data: {ex.Message}");
                return;
            }

            string matFullDir = Path.GetFullPath(matAssetDir);
            if (!Directory.Exists(matFullDir))
            {
                Debug.LogWarning($"[CompositeScene] Materials directory not found: {matFullDir}");
                return;
            }

            string[] matFiles = Directory.GetFiles(matFullDir, "*.mat");
            Debug.Log($"[CompositeScene] Found {matFiles.Length} .mat files in {matFullDir}");
            if (matFiles.Length > 0 && matFiles.Length <= 5)
            {
                foreach (string f in matFiles)
                    Debug.Log($"[CompositeScene]   .mat file: {Path.GetFileName(f)}");
            }

            int patched = 0;
            int notFound = 0;
            foreach (var kvp in matColors)
            {
                string matName = kvp.Key;
                float[] rgba = kvp.Value;

                string matPath = FindMatFile(matFullDir, matName);
                if (matPath == null)
                {
                    notFound++;
                    if (notFound <= 3)
                        Debug.LogWarning($"[CompositeScene] No .mat found for GLB material: '{matName}'");
                    continue;
                }

                string matAssetPath = matAssetDir + "/" + Path.GetFileName(matPath);
                var mat = AssetDatabase.LoadAssetAtPath<Material>(matAssetPath);
                if (mat == null)
                {
                    Debug.LogWarning($"[CompositeScene] Could not load material at: {matAssetPath}");
                    continue;
                }

                var color = new Color(rgba[0], rgba[1], rgba[2], rgba.Length > 3 ? rgba[3] : 1f);

                if (mat.HasProperty("_BaseColor"))
                    mat.SetColor("_BaseColor", color);
                if (mat.HasProperty("_Color"))
                    mat.SetColor("_Color", color);

                EditorUtility.SetDirty(mat);
                patched++;
            }

            if (notFound > 0)
                Debug.LogWarning($"[CompositeScene] {notFound} GLB materials had no matching .mat file.");

            if (patched > 0)
            {
                AssetDatabase.SaveAssets();
                Debug.Log($"[CompositeScene] Patched tint colors on {patched} materials.");
            }
            else
            {
                Debug.LogWarning($"[CompositeScene] No materials were patched! ({matColors.Count} GLB materials, {matFiles.Length} .mat files)");
            }
        }

        private static string FindMatFile(string directory, string baseName)
        {
            string exact = Path.Combine(directory, baseName + ".mat");
            if (File.Exists(exact))
                return exact;

            foreach (string file in Directory.GetFiles(directory, "*.mat"))
            {
                string fileName = Path.GetFileNameWithoutExtension(file);
                int dotIdx = fileName.LastIndexOf('.');
                if (dotIdx > 0)
                {
                    string stripped = fileName.Substring(0, dotIdx);
                    if (string.Equals(stripped, baseName, StringComparison.OrdinalIgnoreCase))
                        return file;
                }
            }

            return null;
        }

        private static Dictionary<string, float[]> ReadGlbMaterialColors(string glbPath)
        {
            var result = new Dictionary<string, float[]>();

            using (var fs = new FileStream(glbPath, FileMode.Open, FileAccess.Read))
            using (var br = new BinaryReader(fs))
            {
                uint magic = br.ReadUInt32();
                if (magic != 0x46546C67)
                    throw new InvalidDataException("Not a valid GLB file");
                br.ReadUInt32();
                br.ReadUInt32();

                uint chunkLen = br.ReadUInt32();
                uint chunkType = br.ReadUInt32();
                if (chunkType != 0x4E4F534A)
                    throw new InvalidDataException("First GLB chunk is not JSON");

                byte[] jsonBytes = br.ReadBytes((int)chunkLen);
                string json = System.Text.Encoding.UTF8.GetString(jsonBytes);

                ParseMaterialColors(json, result);
            }

            return result;
        }

        private static void ParseMaterialColors(string json, Dictionary<string, float[]> result)
        {
            int matIdx = json.IndexOf("\"materials\"", StringComparison.Ordinal);
            if (matIdx < 0) return;

            int arrStart = json.IndexOf('[', matIdx);
            if (arrStart < 0) return;

            int depth = 0;
            int objStart = -1;
            for (int i = arrStart; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"')
                {
                    i++;
                    while (i < json.Length)
                    {
                        if (json[i] == '\\') { i++; }
                        else if (json[i] == '"') break;
                        i++;
                    }
                }
                else if (c == '[' || c == '{')
                {
                    depth++;
                    if (depth == 2 && c == '{') objStart = i;
                }
                else if (c == ']' || c == '}')
                {
                    if (depth == 2 && c == '}' && objStart >= 0)
                    {
                        string obj = json.Substring(objStart, i - objStart + 1);
                        ParseSingleMaterial(obj, result);
                        objStart = -1;
                    }
                    depth--;
                    if (depth <= 0) break;
                }
            }
        }

        private static void ParseSingleMaterial(string obj, Dictionary<string, float[]> result)
        {
            string name = ExtractStringValue(obj, "name");
            if (string.IsNullOrEmpty(name)) return;

            int factorIdx = obj.IndexOf("\"baseColorFactor\"", StringComparison.Ordinal);
            if (factorIdx < 0)
            {
                result[name] = new float[] { 1f, 1f, 1f, 1f };
                return;
            }

            int arrStart = obj.IndexOf('[', factorIdx);
            int arrEnd = obj.IndexOf(']', arrStart);
            if (arrStart < 0 || arrEnd < 0) return;

            string arrStr = obj.Substring(arrStart + 1, arrEnd - arrStart - 1);
            string[] parts = arrStr.Split(',');
            float[] rgba = new float[4] { 1, 1, 1, 1 };
            for (int i = 0; i < parts.Length && i < 4; i++)
            {
                if (float.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float val))
                    rgba[i] = val;
            }

            result[name] = rgba;
        }

        private static string ExtractStringValue(string json, string key)
        {
            string search = "\"" + key + "\"";
            int idx = json.IndexOf(search, StringComparison.Ordinal);
            if (idx < 0) return null;

            int colonIdx = json.IndexOf(':', idx + search.Length);
            if (colonIdx < 0) return null;

            int quoteStart = json.IndexOf('"', colonIdx + 1);
            if (quoteStart < 0) return null;

            int quoteEnd = json.IndexOf('"', quoteStart + 1);
            if (quoteEnd < 0) return null;

            return json.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
        }
    }
}

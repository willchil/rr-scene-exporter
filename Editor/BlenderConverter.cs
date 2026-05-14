using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using RRSceneExporter;
using Debug = UnityEngine.Debug;

namespace CompositeSceneGenerator
{
    /// <summary>
    /// Converts GLB files to FBX with correct material tints by invoking
    /// Blender headlessly. Blender path discovery lives in <see cref="BlenderLocator"/>.
    /// </summary>
    public static class BlenderConverter
    {
        /// <summary>
        /// Convert a GLB file to FBX using Blender in headless mode, with material tint baking.
        /// </summary>
        /// <param name="blenderPath">Path to blender.exe</param>
        /// <param name="glbPath">Absolute path to the input .glb file</param>
        /// <param name="fbxPath">Absolute path for the output .fbx file</param>
        /// <returns>True on success</returns>
        public static bool ConvertGlbToFbx(string blenderPath, string glbPath, string fbxPath)
        {
            // Find the Python script bundled next to this C# file
            string scriptPath = FindConversionScript();
            if (scriptPath == null)
            {
                Debug.LogError("[BlenderConverter] Could not find glb_to_fbx.py next to the editor scripts.");
                return false;
            }

            string arguments = $"--background --python \"{scriptPath}\" -- \"{glbPath}\" \"{fbxPath}\"";

            Debug.Log($"[BlenderConverter] Running: \"{blenderPath}\" {arguments}");

            var psi = new ProcessStartInfo
            {
                FileName = blenderPath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                using (var proc = Process.Start(psi))
                {
                    string stdout = proc.StandardOutput.ReadToEnd();
                    string stderr = proc.StandardError.ReadToEnd();
                    proc.WaitForExit();

                    if (!string.IsNullOrEmpty(stdout))
                        Debug.Log($"[BlenderConverter] stdout:\n{stdout}");
                    if (!string.IsNullOrEmpty(stderr))
                        Debug.LogWarning($"[BlenderConverter] stderr:\n{stderr}");

                    if (proc.ExitCode != 0)
                    {
                        Debug.LogError($"[BlenderConverter] Blender exited with code {proc.ExitCode}");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BlenderConverter] Failed to start Blender: {ex.Message}");
                return false;
            }

            if (!File.Exists(fbxPath))
            {
                Debug.LogError($"[BlenderConverter] Blender completed but output file not found: {fbxPath}");
                return false;
            }

            Debug.Log($"[BlenderConverter] Conversion complete: {fbxPath}");
            return true;
        }

        private static string FindConversionScript()
        {
            // The script is at Assets/Editor/CompositeSceneGenerator/glb_to_fbx.py
            // Find it via AssetDatabase so it works regardless of project location
            string[] guids = AssetDatabase.FindAssets("glb_to_fbx t:DefaultAsset");
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith("glb_to_fbx.py", StringComparison.OrdinalIgnoreCase))
                    return Path.GetFullPath(path);
            }

            // Fallback: look relative to script's known package location
            string fallback = Path.GetFullPath("Packages/com.willchil.rr-scene-exporter/Editor/glb_to_fbx.py");
            if (File.Exists(fallback))
                return fallback;

            return null;
        }
    }
}

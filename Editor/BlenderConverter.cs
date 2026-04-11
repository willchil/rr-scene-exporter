using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace CompositeSceneGenerator
{
    /// <summary>
    /// Finds Blender on the system and converts GLB files to FBX with correct material tints.
    /// </summary>
    public static class BlenderConverter
    {
        /// <summary>
        /// Auto-detect the Blender executable path.
        /// </summary>
        public static string FindBlenderPath()
        {
            // Check common Windows install locations
            string[] candidates = new[]
            {
                // Steam
                @"C:\Program Files (x86)\Steam\steamapps\common\Blender\blender.exe",
                // Blender Foundation default installs (scan for latest version)
                null, // placeholder — scanned below
                // winget / Microsoft Store
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Programs\Blender Foundation\Blender\blender.exe"),
            };

            // Check explicit paths first
            foreach (string path in candidates)
            {
                if (path != null && File.Exists(path))
                    return path;
            }

            // Scan Blender Foundation folders for versioned installs (e.g. "Blender 4.2")
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string blenderFoundation = Path.Combine(programFiles, "Blender Foundation");
            if (Directory.Exists(blenderFoundation))
            {
                // Sort descending to prefer the latest version
                string[] dirs = Directory.GetDirectories(blenderFoundation);
                Array.Sort(dirs);
                Array.Reverse(dirs);
                foreach (string dir in dirs)
                {
                    string exe = Path.Combine(dir, "blender.exe");
                    if (File.Exists(exe))
                        return exe;
                }
            }

            // Try PATH
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "where",
                    Arguments = "blender",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var proc = Process.Start(psi))
                {
                    string output = proc.StandardOutput.ReadToEnd().Trim();
                    proc.WaitForExit();
                    if (proc.ExitCode == 0 && !string.IsNullOrEmpty(output))
                    {
                        string firstLine = output.Split('\n')[0].Trim();
                        if (File.Exists(firstLine))
                            return firstLine;
                    }
                }
            }
            catch
            {
                // Ignore — where.exe might not be available
            }

            return null;
        }

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

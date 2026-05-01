using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace RRSceneExporter.RRAvatar
{
    /// <summary>
    /// Drives the bundled Blender script that turns a Rec Room avatar GLB into a
    /// rigged FBX bound to fb_library.blend's Avatar_Skeleton.
    /// </summary>
    public static class AvatarConverter
    {
        private const string PackageRoot = "Packages/com.willchil.rr-scene-exporter/Editor/RRAvatar";

        /// <summary>Auto-detect the Blender executable path on Windows.</summary>
        public static string FindBlenderPath()
        {
            string[] candidates =
            {
                @"C:\Program Files (x86)\Steam\steamapps\common\Blender\blender.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Programs\Blender Foundation\Blender\blender.exe"),
            };
            foreach (string p in candidates)
            {
                if (!string.IsNullOrEmpty(p) && File.Exists(p))
                    return p;
            }

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string foundation = Path.Combine(programFiles, "Blender Foundation");
            if (Directory.Exists(foundation))
            {
                string[] dirs = Directory.GetDirectories(foundation);
                Array.Sort(dirs);
                Array.Reverse(dirs);
                foreach (string dir in dirs)
                {
                    string exe = Path.Combine(dir, "blender.exe");
                    if (File.Exists(exe))
                        return exe;
                }
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "where",
                    Arguments = "blender",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
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
                // where.exe might not be available
            }

            return null;
        }

        /// <summary>
        /// Run Blender headlessly to import <paramref name="glbPath"/> into fb_library.blend,
        /// transfer weights, and write the rigged result to <paramref name="fbxPath"/>.
        /// </summary>
        public static bool ConvertGlbToRiggedFbx(string blenderPath, string glbPath, string fbxPath)
        {
            string scriptPath = ResolvePackageFile("avatar_convert.py");
            string blendPath = ResolvePackageFile("fb_library.blend");

            if (scriptPath == null)
            {
                Debug.LogError("[AvatarConverter] avatar_convert.py not found in package.");
                return false;
            }
            if (blendPath == null)
            {
                Debug.LogError("[AvatarConverter] fb_library.blend not found in package.");
                return false;
            }

            string args = $"\"{blendPath}\" --background --factory-startup --python \"{scriptPath}\" -- \"{glbPath}\" \"{fbxPath}\"";
            Debug.Log($"[AvatarConverter] Running: \"{blenderPath}\" {args}");

            // Redirect Blender's user-resources lookup to an empty temp dir so
            // any user-installed addons (notably rr_avatar_tools, which calls
            // GPU shader APIs at register-time and crashes in --background) are
            // not discovered. --factory-startup alone is not enough in 4.2.
            string emptyResources = Path.Combine(Path.GetTempPath(), "blender-empty-resources");
            Directory.CreateDirectory(emptyResources);

            var psi = new ProcessStartInfo
            {
                FileName = blenderPath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.EnvironmentVariables["BLENDER_USER_RESOURCES"] = emptyResources;
            psi.EnvironmentVariables["BLENDER_USER_SCRIPTS"]   = emptyResources;
            psi.EnvironmentVariables["BLENDER_USER_CONFIG"]    = emptyResources;

            try
            {
                using (var proc = Process.Start(psi))
                {
                    string stdout = proc.StandardOutput.ReadToEnd();
                    string stderr = proc.StandardError.ReadToEnd();
                    proc.WaitForExit();

                    if (!string.IsNullOrEmpty(stdout))
                        Debug.Log($"[AvatarConverter] stdout:\n{stdout}");
                    if (!string.IsNullOrEmpty(stderr))
                        Debug.LogWarning($"[AvatarConverter] stderr:\n{stderr}");

                    if (proc.ExitCode != 0)
                    {
                        Debug.LogError($"[AvatarConverter] Blender exited with code {proc.ExitCode}");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AvatarConverter] Failed to start Blender: {ex.Message}");
                return false;
            }

            if (!File.Exists(fbxPath))
            {
                Debug.LogError($"[AvatarConverter] Blender completed but output file not found: {fbxPath}");
                return false;
            }

            Debug.Log($"[AvatarConverter] Wrote {fbxPath}");
            return true;
        }

        private static string ResolvePackageFile(string fileName)
        {
            string packageRel = PackageRoot + "/" + fileName;
            string fullPath = Path.GetFullPath(packageRel);
            if (File.Exists(fullPath))
                return fullPath;

            string[] guids = AssetDatabase.FindAssets(Path.GetFileNameWithoutExtension(fileName));
            foreach (string guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (assetPath.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
                    return Path.GetFullPath(assetPath);
            }
            return null;
        }
    }
}

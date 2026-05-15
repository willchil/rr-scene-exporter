using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace RRSceneExporter
{
    /// <summary>
    /// Locates the Blender executable on Windows and macOS. Collects every install
    /// we can find (Steam, per-user Programs, system Program Files, PATH
    /// lookup), queries each one's <c>--version</c>, and returns the
    /// newest. We deliberately do not return the first candidate that
    /// happens to exist, because users frequently have an old Steam or
    /// system install hanging around alongside a current one and we need
    /// the newer Blender to load avatar_convert.py's modern bpy API.
    /// </summary>
    public static class BlenderLocator
    {
        public static string FindBlenderPath()
        {
            var candidates = new List<string>();

#if UNITY_EDITOR_WIN
            // Steam installs (unversioned single directory).
            candidates.Add(@"C:\Program Files (x86)\Steam\steamapps\common\Blender\blender.exe");
            candidates.Add(@"C:\Program Files\Steam\steamapps\common\Blender\blender.exe");

            // Per-user installer (recent Blender installer default; no version
            // suffix in the directory name).
            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Programs\Blender Foundation\Blender\blender.exe"));

            // Versioned per-machine installs from the .msi installer, in any
            // of the well-known Blender Foundation parent directories.
            string[] foundationParents =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Blender Foundation"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Blender Foundation"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Programs\Blender Foundation"),
            };
            foreach (string parent in foundationParents)
            {
                if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
                    continue;
                foreach (string dir in Directory.GetDirectories(parent))
                {
                    string exe = Path.Combine(dir, "blender.exe");
                    if (File.Exists(exe))
                        candidates.Add(exe);
                }
            }
#elif UNITY_EDITOR_OSX
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (string root in new[] { "/Applications", Path.Combine(home, "Applications") })
            {
                if (!Directory.Exists(root))
                    continue;
                try
                {
                    foreach (string app in Directory.GetDirectories(root, "Blender*.app"))
                        candidates.Add(Path.Combine(app, "Contents/MacOS/Blender"));
                }
                catch { }
            }
            candidates.Add(Path.Combine(home,
                "Library/Application Support/Steam/steamapps/common/Blender/Blender.app/Contents/MacOS/Blender"));
#endif

            // Anything resolvable via PATH (scoop, chocolatey, manual symlinks, ...).
            candidates.AddRange(WhereBlender());

            // Filter to existing, deduped, normalised paths.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var existing = new List<string>();
            foreach (string p in candidates)
            {
                if (string.IsNullOrEmpty(p) || !File.Exists(p))
                    continue;
                string full;
                try { full = Path.GetFullPath(p); }
                catch { continue; }
                if (seen.Add(full))
                    existing.Add(full);
            }

            if (existing.Count == 0)
                return null;
            if (existing.Count == 1)
                return existing[0];

            // Pick the highest version. Falls back to the first existing path
            // if none of the installs respond to --version (e.g. permissions
            // issues, antivirus interference).
            string best = null;
            Version bestVer = null;
            foreach (string p in existing)
            {
                Version v = QueryBlenderVersion(p);
                if (v == null)
                    continue;
                if (bestVer == null || v > bestVer)
                {
                    bestVer = v;
                    best = p;
                }
            }
            return best ?? existing[0];
        }

        public static string NormalizeBlenderPath(string path)
        {
#if UNITY_EDITOR_OSX
            if (!string.IsNullOrEmpty(path) && path.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                string inner = Path.Combine(path, "Contents/MacOS/Blender");
                if (File.Exists(inner))
                    return inner;
            }
#endif
            return path;
        }

        private static IEnumerable<string> WhereBlender()
        {
            string output = null;
            try
            {
                var psi = new ProcessStartInfo
                {
#if UNITY_EDITOR_WIN
                    FileName = "where",
#else
                    FileName = "/usr/bin/which",
#endif
                    Arguments = "blender",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using (var proc = Process.Start(psi))
                {
                    output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit();
                    if (proc.ExitCode != 0)
                        output = null;
                }
            }
            catch
            {
                output = null;
            }
            if (string.IsNullOrEmpty(output))
                yield break;
            foreach (string line in output.Split('\n'))
            {
                string t = line.Trim();
                if (!string.IsNullOrEmpty(t))
                    yield return t;
            }
        }

        // Match either "Blender 4.2.13" from --version output or the more
        // verbose "Blender 4.2.13 LTS (...)" form.
        private static readonly Regex BlenderVersionRegex =
            new Regex(@"Blender\s+(\d+)\.(\d+)(?:\.(\d+))?", RegexOptions.IgnoreCase);

        private static Version QueryBlenderVersion(string blenderExe)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = blenderExe,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using (var proc = Process.Start(psi))
                {
                    string output = proc.StandardOutput.ReadToEnd();
                    if (!proc.WaitForExit(5000))
                    {
                        try { proc.Kill(); } catch { /* best-effort */ }
                        return null;
                    }
                    Match m = BlenderVersionRegex.Match(output);
                    if (!m.Success)
                        return null;
                    int major = int.Parse(m.Groups[1].Value);
                    int minor = int.Parse(m.Groups[2].Value);
                    int patch = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
                    return new Version(major, minor, patch);
                }
            }
            catch
            {
                return null;
            }
        }
    }
}

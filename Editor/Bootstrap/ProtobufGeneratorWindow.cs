using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace CompositeSceneGenerator.Bootstrap
{
    /// <summary>
    /// Creates a stub asmdef for the Generated assembly on first load so that the
    /// main Editor assembly's reference resolves before protos have been generated.
    /// Without this, the dangling reference causes a compile error that also
    /// prevents the Bootstrap menu items from appearing.
    /// </summary>
    [InitializeOnLoad]
    static class GeneratedAssemblyStub
    {
        private const string StubDir = "Assets/RecRoomCache/Generated";
        private const string AsmdefName = "willchil.RRSceneExporter.Generated.asmdef";

        static GeneratedAssemblyStub()
        {
            string asmdefPath = Path.Combine(Path.GetFullPath(StubDir), AsmdefName);
            if (!File.Exists(asmdefPath))
                EditorApplication.delayCall += CreateStubAsmdef;
        }

        private static void CreateStubAsmdef()
        {
            EnsureAsmdef(Path.GetFullPath(StubDir));
            AssetDatabase.Refresh();
        }

        /// <summary>
        /// Ensures the Generated assembly definition file exists in the given directory.
        /// Called both at editor startup (stub) and during proto generation.
        /// </summary>
        internal static void EnsureAsmdef(string fullDir)
        {
            string asmdefPath = Path.Combine(fullDir, AsmdefName);
            if (File.Exists(asmdefPath))
                return;

            Directory.CreateDirectory(fullDir);
            File.WriteAllText(asmdefPath, AsmdefContent);
        }

        internal const string AsmdefContent = @"{
    ""name"": ""willchil.RRSceneExporter.Generated"",
    ""rootNamespace"": """",
    ""references"": [],
    ""precompiledReferences"": [
        ""Google.Protobuf.dll""
    ],
    ""autoReferenced"": true,
    ""allowUnsafeCode"": false,
    ""noEngineReferences"": true,
    ""overrideReferences"": true,
    ""defineConstraints"": [],
    ""includePlatforms"": [],
    ""excludePlatforms"": [],
    ""versionDefines"": []
}";
    }

    public class ProtobufGeneratorWindow : EditorWindow
    {
        [SerializeField] private DefaultAsset descriptorSet;
        [SerializeField] private string protocPath;

        private const string GeneratedDir = "Assets/RecRoomCache/Generated";
        private const string ScriptingDefine = "RECROOM_PROTOS_GENERATED";

        [MenuItem("Rec Room Exporter/Generate Protobuf Classes")]
        public static void ShowWindow()
        {
            GetWindow<ProtobufGeneratorWindow>("Protobuf Generator");
        }

        private void OnEnable()
        {
            if (string.IsNullOrEmpty(protocPath))
                protocPath = FindProtocPath() ?? "";
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Protobuf Class Generator", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            EditorGUILayout.HelpBox(
                "Generate C# protobuf classes from a descriptor_set.binpb file. " +
                "This is required before the Composite Scene Generator can be used.",
                MessageType.Info);
            EditorGUILayout.Space();

            descriptorSet = (DefaultAsset)EditorGUILayout.ObjectField(
                new GUIContent("Descriptor Set", "The descriptor_set.binpb file containing protobuf schemas."),
                descriptorSet, typeof(DefaultAsset), false);

            EditorGUILayout.BeginHorizontal();
            protocPath = EditorGUILayout.TextField(
                new GUIContent("protoc Path", "Path to protoc.exe."),
                protocPath);
            if (GUILayout.Button("Browse", GUILayout.Width(60)))
            {
                string selected = EditorUtility.OpenFilePanel("Select protoc Executable", "", "exe");
                if (!string.IsNullOrEmpty(selected))
                    protocPath = selected;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();

            // Status
            string fullDir = Path.GetFullPath(GeneratedDir);
            bool alreadyGenerated = Directory.Exists(fullDir)
                && Directory.GetFiles(fullDir, "*.cs", SearchOption.AllDirectories).Length > 0;

            if (alreadyGenerated)
            {
                int count = Directory.GetFiles(fullDir, "*.cs", SearchOption.AllDirectories).Length;
                EditorGUILayout.HelpBox(
                    $"Generated classes already exist ({count} files). " +
                    "Click Generate to regenerate them.",
                    MessageType.None);
            }

            // Validation
            bool valid = true;
            if (descriptorSet == null)
            {
                EditorGUILayout.HelpBox("Assign the descriptor_set.binpb file.", MessageType.Warning);
                valid = false;
            }
            else
            {
                string descAssetPath = AssetDatabase.GetAssetPath(descriptorSet);
                if (!descAssetPath.EndsWith(".binpb", StringComparison.OrdinalIgnoreCase))
                {
                    EditorGUILayout.HelpBox("File should be a .binpb file.", MessageType.Warning);
                    valid = false;
                }
            }

            if (string.IsNullOrEmpty(protocPath) || !File.Exists(protocPath))
            {
                EditorGUILayout.HelpBox(
                    "protoc executable not found. Install it via the command line with 'winget install protobuf', " +
                    "or download it from GitHub and set the path above.",
                    MessageType.Warning);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Download protoc"))
                    Application.OpenURL("https://github.com/protocolbuffers/protobuf/releases");
                if (GUILayout.Button("Check for installation"))
                    protocPath = FindProtocPath() ?? "";
                EditorGUILayout.EndHorizontal();
                valid = false;
            }

            EditorGUI.BeginDisabledGroup(!valid);
            if (GUILayout.Button("Generate", GUILayout.Height(30)))
            {
                GenerateProtos();
            }
            EditorGUI.EndDisabledGroup();
        }

        private void GenerateProtos()
        {
            string descPath = Path.GetFullPath(AssetDatabase.GetAssetPath(descriptorSet));
            string fullDir = Path.GetFullPath(GeneratedDir);

            try
            {
                // Parse the descriptor set to get all proto file names
                byte[] descBytes = File.ReadAllBytes(descPath);
                var fds = Google.Protobuf.Reflection.FileDescriptorSet.Parser.ParseFrom(descBytes);
                var fileNames = new List<string>();
                foreach (var file in fds.File)
                {
                    if (!string.IsNullOrEmpty(file.Name))
                        fileNames.Add(file.Name);
                }

                if (fileNames.Count == 0)
                {
                    Debug.LogError("[ProtobufGenerator] Descriptor set contains no proto files.");
                    return;
                }

                Debug.Log($"[ProtobufGenerator] Generating C# from {fileNames.Count} proto files...");
                EditorUtility.DisplayProgressBar("Generating Protobuf Classes",
                    $"Running protoc on {fileNames.Count} files...", 0.2f);

                Directory.CreateDirectory(fullDir);

                // Write the assembly definition so Unity compiles the generated code
                WriteGeneratedAsmdef(fullDir);

                // Build arguments — batch if command line would be too long
                string baseArgs = $"--descriptor_set_in=\"{descPath}\" --csharp_out=\"{fullDir}\" --csharp_opt=base_namespace=";
                const int maxArgLength = 30000;

                var batch = new List<string>();
                int currentLength = baseArgs.Length;

                for (int i = 0; i < fileNames.Count; i++)
                {
                    string name = fileNames[i];
                    if (currentLength + name.Length + 1 > maxArgLength && batch.Count > 0)
                    {
                        if (!RunProtoc(baseArgs + " " + string.Join(" ", batch)))
                            return;
                        batch.Clear();
                        currentLength = baseArgs.Length;
                    }
                    batch.Add(name);
                    currentLength += name.Length + 1;
                }

                if (batch.Count > 0 && !RunProtoc(baseArgs + " " + string.Join(" ", batch)))
                    return;

                EditorUtility.DisplayProgressBar("Generating Protobuf Classes", "Refreshing assets...", 0.9f);
                AssetDatabase.Refresh();

                int generated = Directory.GetFiles(fullDir, "*.cs", SearchOption.AllDirectories).Length;
                Debug.Log($"[ProtobufGenerator] Generated {generated} C# files in {GeneratedDir}.");

                // Add scripting define so the main assembly compiles
                AddScriptingDefine(ScriptingDefine);

                Close();

                EditorUtility.DisplayDialog("Generation Complete",
                    $"Generated {generated} C# protobuf classes.\n\n" +
                    "Unity will recompile. Once finished, open the Composite Scene Generator " +
                    "from Rec Room > Generate Composite Scene.", "OK");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ProtobufGenerator] Error: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private bool RunProtoc(string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = protocPath,
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

                    if (proc.ExitCode != 0)
                    {
                        Debug.LogError($"[ProtobufGenerator] protoc failed (exit {proc.ExitCode}):\n{stderr}");
                        EditorUtility.ClearProgressBar();
                        return false;
                    }

                    if (!string.IsNullOrEmpty(stderr))
                        Debug.LogWarning($"[ProtobufGenerator] protoc warnings:\n{stderr}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ProtobufGenerator] Failed to start protoc: {ex.Message}");
                EditorUtility.ClearProgressBar();
                return false;
            }

            return true;
        }

        private static string FindProtocPath()
        {
            string tempProtoc = Path.Combine(Path.GetTempPath(), "protoc", "bin", "protoc.exe");
            if (File.Exists(tempProtoc))
                return tempProtoc;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "where",
                    Arguments = "protoc",
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
            catch { }

            return null;
        }

        private static void AddScriptingDefine(string define)
        {
            var target = EditorUserBuildSettings.selectedBuildTargetGroup;
            if (target == BuildTargetGroup.Unknown)
                target = BuildTargetGroup.Standalone;

            string defines = PlayerSettings.GetScriptingDefineSymbolsForGroup(target);
            if (defines.Contains(define))
                return;

            defines = string.IsNullOrEmpty(defines) ? define : defines + ";" + define;
            PlayerSettings.SetScriptingDefineSymbolsForGroup(target, defines);
        }

        private static void WriteGeneratedAsmdef(string fullDir)
        {
            GeneratedAssemblyStub.EnsureAsmdef(fullDir);
        }
    }
}

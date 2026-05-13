using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace RRSceneExporter.RRAvatar
{
    public enum WatchHand
    {
        Left,
        Right,
        None,
        Both,
    }

    public class ConvertAvatarWindow : EditorWindow
    {
        [SerializeField] private string blenderPath;
        [SerializeField] private DefaultAsset glbAsset;
        [SerializeField] private WatchHand watchHand = WatchHand.Left;
        [SerializeField] private List<string> rigidMeshes = new List<string>();

        private DefaultAsset lastScannedGlb;
        private List<string> meshNames = new List<string>();
        private Vector2 rigidScroll;

        [MenuItem("Rec Room/Convert Avatar")]
        public static void ShowWindow()
        {
            GetWindow<ConvertAvatarWindow>("Convert Avatar");
        }

        private void OnEnable()
        {
            if (string.IsNullOrEmpty(blenderPath))
                blenderPath = AvatarConverter.FindBlenderPath() ?? "";
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Rec Room Avatar Converter", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            EditorGUILayout.BeginHorizontal();
            blenderPath = EditorGUILayout.TextField(
                new GUIContent("Blender Path", "Path to blender.exe. Auto-detected from common install locations."),
                blenderPath);
            if (GUILayout.Button("Browse", GUILayout.Width(60)))
            {
                string selected = EditorUtility.OpenFilePanel("Select Blender Executable", "", "exe");
                if (!string.IsNullOrEmpty(selected))
                    blenderPath = selected;
            }
            EditorGUILayout.EndHorizontal();

            glbAsset = (DefaultAsset)EditorGUILayout.ObjectField(
                new GUIContent("Avatar GLB (A-Pose)", "The .glb avatar file exported from Rec Room. Must be exported as an A-Pose."),
                glbAsset, typeof(DefaultAsset), false);

            if (glbAsset != lastScannedGlb)
            {
                RefreshMeshNames();
            }

            if (glbAsset != null && meshNames.Count > 0)
            {
                EditorGUILayout.Space();
                watchHand = (WatchHand)EditorGUILayout.EnumPopup(
                    new GUIContent("Watch Hand",
                        "Which wrist the watch should appear on. The Wrist_Watch_* meshes on the " +
                        "unselected side(s) are deleted from the avatar before rigging."),
                    watchHand);

                EditorGUILayout.Space();
                EditorGUILayout.LabelField(
                    new GUIContent("Rigid Meshes",
                        "Meshes checked here are bound rigidly to a single nearest bone instead of " +
                        "having body weights transferred onto them. Use for accessories that should " +
                        "not deform with the skeleton (e.g. watches, glasses, props)."),
                    EditorStyles.boldLabel);

                rigidScroll = EditorGUILayout.BeginScrollView(rigidScroll, GUILayout.MaxHeight(150));
                var deleteSet = new HashSet<string>(ComputeDeleteMeshes());
                foreach (string name in meshNames)
                {
                    if (deleteSet.Contains(name))
                        continue;
                    bool was = rigidMeshes.Contains(name);
                    bool now = EditorGUILayout.ToggleLeft(name, was);
                    if (now && !was)
                        rigidMeshes.Add(name);
                    else if (!now && was)
                        rigidMeshes.Remove(name);
                }
                EditorGUILayout.EndScrollView();
            }

            EditorGUILayout.Space();

            bool valid = true;

            if (string.IsNullOrEmpty(blenderPath) || !File.Exists(blenderPath))
            {
                EditorGUILayout.HelpBox(
                    "A valid Blender executable is required. Install Blender and set the path above.",
                    MessageType.Warning);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Download Blender"))
                    Application.OpenURL("https://www.blender.org/download/");
                if (GUILayout.Button("Check for installation"))
                    blenderPath = AvatarConverter.FindBlenderPath() ?? "";
                EditorGUILayout.EndHorizontal();
                valid = false;
            }

            if (glbAsset == null)
            {
                EditorGUILayout.HelpBox("Select a .glb (or .gltf) avatar file.", MessageType.Warning);
                valid = false;
            }
            else
            {
                string p = AssetDatabase.GetAssetPath(glbAsset);
                if (!p.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) &&
                    !p.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase))
                {
                    EditorGUILayout.HelpBox("File must be a .glb or .gltf.", MessageType.Warning);
                    valid = false;
                }
            }

            EditorGUI.BeginDisabledGroup(!valid);
            if (GUILayout.Button("Convert Avatar", GUILayout.Height(30)))
            {
                ConvertAvatar();
            }
            EditorGUI.EndDisabledGroup();
        }

        private void ConvertAvatar()
        {
            string glbAssetPath = AssetDatabase.GetAssetPath(glbAsset);
            if (string.IsNullOrEmpty(glbAssetPath))
            {
                EditorUtility.DisplayDialog("Convert Avatar", "Could not resolve GLB asset path.", "OK");
                return;
            }

            string glbFullPath = Path.GetFullPath(glbAssetPath);
            string fbxAssetPath = Path.ChangeExtension(glbAssetPath, ".fbx");
            string fbxFullPath = Path.GetFullPath(fbxAssetPath);

            if (File.Exists(fbxFullPath))
            {
                bool overwrite = EditorUtility.DisplayDialog(
                    "Convert Avatar",
                    $"An FBX already exists at:\n{fbxAssetPath}\n\nOverwrite it?",
                    "Overwrite", "Cancel");
                if (!overwrite)
                    return;
            }

            bool ok;
            try
            {
                EditorUtility.DisplayProgressBar(
                    "Convert Avatar",
                    "Running Blender (rig + export)...",
                    0.5f);
                ok = AvatarConverter.ConvertGlbToRiggedFbx(
                    blenderPath, glbFullPath, fbxFullPath,
                    rigidMeshes.Where(n => !ComputeDeleteMeshes().Contains(n)),
                    ComputeDeleteMeshes());
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (!ok)
            {
                EditorUtility.DisplayDialog(
                    "Convert Avatar",
                    "Conversion failed. See the Console for details.", "OK");
                return;
            }

            // Import the unpacked texture folder first and flag normal maps
            // before the FBX is imported
            string texAssetDir = Path.ChangeExtension(glbAssetPath, null) + "_Textures";
            if (Directory.Exists(Path.GetFullPath(texAssetDir)))
            {
                AssetDatabase.ImportAsset(
                    texAssetDir,
                    ImportAssetOptions.ImportRecursive | ImportAssetOptions.ForceSynchronousImport);
                MarkNormalMapTextures(texAssetDir);
            }

            AssetDatabase.ImportAsset(fbxAssetPath, ImportAssetOptions.ForceSynchronousImport);

            var importer = AssetImporter.GetAtPath(fbxAssetPath) as ModelImporter;
            if (importer != null)
            {
                importer.animationType = ModelImporterAnimationType.Human;
                importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                importer.importAnimation = false;
                importer.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;

                // Build a HumanDescription that maps Rec Room's Jnt.* bones to
                // Unity's humanoid slots. Without this, Unity's auto-mapper
                // mostly fails (the bones aren't named LeftUpperArm etc.).
                ApplyHumanoidMapping(importer, fbxAssetPath);

                importer.SaveAndReimport();

                ExtractMaterials(fbxAssetPath, glbAssetPath);

                importer.SaveAndReimport();
            }

            var imported = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(fbxAssetPath);
            if (imported != null)
            {
                EditorGUIUtility.PingObject(imported);
                Selection.activeObject = imported;
            }

            EditorUtility.DisplayDialog(
                "Convert Avatar",
                $"Avatar exported to:\n{fbxAssetPath}",
                "OK");
        }

        // ----- Rigid-mesh scan ---------------------------------------------------

        /// <summary>
        /// Compute the set of GLB mesh names that should be deleted from the avatar
        /// before rigging, based on the current ``watchHand`` selection. Names are
        /// matched against the scanned ``meshNames`` list (prefix match on
        /// ``Wrist_Watch_L`` / ``Wrist_Watch_R``).
        /// </summary>
        private IEnumerable<string> ComputeDeleteMeshes()
        {
            bool deleteLeft  = watchHand == WatchHand.Right || watchHand == WatchHand.None;
            bool deleteRight = watchHand == WatchHand.Left  || watchHand == WatchHand.None;

            foreach (string name in meshNames)
            {
                if (deleteLeft && name.StartsWith("Wrist_Watch_L_", StringComparison.Ordinal))
                    yield return name;
                else if (deleteRight && name.StartsWith("Wrist_Watch_R_", StringComparison.Ordinal))
                    yield return name;
            }
        }

        /// <summary>
        /// Scan the currently selected GLB and populate ``meshNames`` with the names
        /// of every node that references a mesh. Also prunes ``rigidMeshes`` of any
        /// entries that no longer exist in the new GLB.
        /// </summary>
        private void RefreshMeshNames()
        {
            lastScannedGlb = glbAsset;
            meshNames.Clear();

            if (glbAsset == null)
            {
                rigidMeshes.Clear();
                return;
            }

            string assetPath = AssetDatabase.GetAssetPath(glbAsset);
            string fullPath = string.IsNullOrEmpty(assetPath) ? null : Path.GetFullPath(assetPath);
            if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
                return;

            try
            {
                meshNames.AddRange(ReadGlbMeshNodeNames(fullPath));
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[ConvertAvatar] Failed to scan GLB '{fullPath}': {ex.Message}");
            }

            // Drop any previously selected names that are no longer present.
            rigidMeshes.RemoveAll(n => !meshNames.Contains(n));

            // Default any Wrist_Watch_* meshes to rigid (the watch is a solid
            // accessory that should follow the wrist bone without deforming).
            foreach (string name in meshNames)
            {
                if (name.StartsWith("Wrist_Watch_", StringComparison.Ordinal) &&
                    !rigidMeshes.Contains(name))
                {
                    rigidMeshes.Add(name);
                }
            }
        }

        /// <summary>
        /// Parse a .glb file's JSON chunk and return the names of every node that
        /// references a mesh (i.e. has a ``mesh`` property). These match the
        /// Blender object names produced by ``bpy.ops.import_scene.gltf``.
        /// </summary>
        private static IEnumerable<string> ReadGlbMeshNodeNames(string glbPath)
        {
            byte[] bytes = File.ReadAllBytes(glbPath);
            if (bytes.Length < 20 ||
                bytes[0] != (byte)'g' || bytes[1] != (byte)'l' ||
                bytes[2] != (byte)'T' || bytes[3] != (byte)'F')
            {
                throw new InvalidDataException("Not a binary glTF file.");
            }

            uint jsonLen = BitConverter.ToUInt32(bytes, 12);
            string chunkType = System.Text.Encoding.ASCII.GetString(bytes, 16, 4);
            // glTF spec spells the JSON chunk type "JSON" padded with a space to 4 bytes.
            if (!chunkType.StartsWith("JSON"))
                throw new InvalidDataException($"First GLB chunk is not JSON (got '{chunkType}').");
            if (20 + jsonLen > bytes.Length)
                throw new InvalidDataException("GLB JSON chunk extends past end of file.");

            string json = System.Text.Encoding.UTF8.GetString(bytes, 20, (int)jsonLen);

            // JsonUtility is unreliable on glTF (extensions/extras blocks, etc.),
            // so do a minimal hand-rolled scan: locate the "nodes" array, then
            // pull each top-level object that contains both a "name" and a
            // "mesh" property.
            var names = new List<string>();
            int nodesStart = FindArrayStart(json, "nodes");
            if (nodesStart < 0)
                return names;

            int i = nodesStart + 1; // skip '['
            int depth = 1;
            int objStart = -1;
            int objDepth = 0;
            bool inString = false;
            bool escape = false;
            while (i < json.Length && depth > 0)
            {
                char c = json[i];
                if (inString)
                {
                    if (escape) escape = false;
                    else if (c == '\\') escape = true;
                    else if (c == '"') inString = false;
                }
                else if (c == '"') inString = true;
                else if (c == '{')
                {
                    if (depth == 1)
                    {
                        objStart = i;
                        objDepth = 1;
                    }
                    else objDepth++;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (objStart >= 0)
                    {
                        objDepth--;
                        if (objDepth == 0)
                        {
                            string body = json.Substring(objStart, i - objStart + 1);
                            string name = ExtractStringProp(body, "name");
                            if (!string.IsNullOrEmpty(name) && HasNumericProp(body, "mesh"))
                                names.Add(name);
                            objStart = -1;
                        }
                    }
                }
                else if (c == '[') depth++;
                else if (c == ']') depth--;
                i++;
            }
            return names;
        }

        private static int FindArrayStart(string json, string key)
        {
            // Find `"<key>"` followed by optional whitespace, ':', whitespace, then '['.
            string token = "\"" + key + "\"";
            int idx = 0;
            while ((idx = json.IndexOf(token, idx, StringComparison.Ordinal)) >= 0)
            {
                int p = idx + token.Length;
                while (p < json.Length && char.IsWhiteSpace(json[p])) p++;
                if (p < json.Length && json[p] == ':')
                {
                    p++;
                    while (p < json.Length && char.IsWhiteSpace(json[p])) p++;
                    if (p < json.Length && json[p] == '[')
                        return p;
                }
                idx = p;
            }
            return -1;
        }

        private static string ExtractStringProp(string body, string key)
        {
            string token = "\"" + key + "\"";
            int idx = body.IndexOf(token, StringComparison.Ordinal);
            if (idx < 0) return null;
            int p = idx + token.Length;
            while (p < body.Length && char.IsWhiteSpace(body[p])) p++;
            if (p >= body.Length || body[p] != ':') return null;
            p++;
            while (p < body.Length && char.IsWhiteSpace(body[p])) p++;
            if (p >= body.Length || body[p] != '"') return null;
            p++;
            var sb = new System.Text.StringBuilder();
            while (p < body.Length)
            {
                char c = body[p];
                if (c == '\\' && p + 1 < body.Length)
                {
                    char n = body[p + 1];
                    if (n == '"' || n == '\\' || n == '/') sb.Append(n);
                    else if (n == 'n') sb.Append('\n');
                    else if (n == 't') sb.Append('\t');
                    else if (n == 'r') sb.Append('\r');
                    else sb.Append(n);
                    p += 2;
                    continue;
                }
                if (c == '"') return sb.ToString();
                sb.Append(c);
                p++;
            }
            return null;
        }

        private static bool HasNumericProp(string body, string key)
        {
            string token = "\"" + key + "\"";
            int idx = body.IndexOf(token, StringComparison.Ordinal);
            if (idx < 0) return false;
            int p = idx + token.Length;
            while (p < body.Length && char.IsWhiteSpace(body[p])) p++;
            if (p >= body.Length || body[p] != ':') return false;
            p++;
            while (p < body.Length && char.IsWhiteSpace(body[p])) p++;
            return p < body.Length && (char.IsDigit(body[p]) || body[p] == '-');
        }


        // ----- Humanoid mapping --------------------------------------------------

        /// <summary>
        /// Exact map from Unity humanoid slot name to Rec Room ``Jnt.*`` bone
        /// name, derived from the actual Avatar_Skeleton hierarchy. Slots not
        /// present in the rig (UpperChest, Jaw) are simply omitted.
        /// </summary>
        private static readonly (string Slot, string Bone)[] HumanoidBoneMap =
        {
            ("Hips",  "Jnt.Spine.Root"),
            ("Spine", "Jnt.Spine.Mid"),
            ("Chest", "Jnt.Spine.Chest"),
            ("Neck",  "Jnt.Neck"),
            ("Head",  "Jnt.Head"),
            ("LeftEye",  "Jnt.Head.Eye.L"),
            ("RightEye", "Jnt.Head.Eye.R"),

            ("LeftShoulder", "Jnt.Shoulder.L"),
            ("LeftUpperArm", "Jnt.UpperArm.L"),
            ("LeftLowerArm", "Jnt.LowerArm.L"),
            ("LeftHand",     "Jnt.Hand.L"),

            ("RightShoulder", "Jnt.Shoulder.R"),
            ("RightUpperArm", "Jnt.UpperArm.R"),
            ("RightLowerArm", "Jnt.LowerArm.R"),
            ("RightHand",     "Jnt.Hand.R"),

            ("LeftUpperLeg", "Jnt.UpperLeg.L"),
            ("LeftLowerLeg", "Jnt.LowerLeg.L"),
            ("LeftFoot",     "Jnt.Foot.L"),
            ("LeftToes",     "Jnt.Toe.L"),

            ("RightUpperLeg", "Jnt.UpperLeg.R"),
            ("RightLowerLeg", "Jnt.LowerLeg.R"),
            ("RightFoot",     "Jnt.Foot.R"),
            ("RightToes",     "Jnt.Toe.R"),

            ("Left Thumb Proximal",     "Jnt.Hand.Thumb1.L"),
            ("Left Thumb Intermediate", "Jnt.Hand.Thumb2.L"),
            ("Left Thumb Distal",       "Jnt.Hand.Thumb3.L"),
            ("Left Index Proximal",     "Jnt.Hand.Index1.L"),
            ("Left Index Intermediate", "Jnt.Hand.Index2.L"),
            ("Left Index Distal",       "Jnt.Hand.Index3.L"),
            ("Left Middle Proximal",     "Jnt.Hand.Middle1.L"),
            ("Left Middle Intermediate", "Jnt.Hand.Middle2.L"),
            ("Left Middle Distal",       "Jnt.Hand.Middle3.L"),
            ("Left Ring Proximal",     "Jnt.Hand.Ring1.L"),
            ("Left Ring Intermediate", "Jnt.Hand.Ring2.L"),
            ("Left Ring Distal",       "Jnt.Hand.Ring3.L"),
            ("Left Little Proximal",     "Jnt.Hand.Pinky1.L"),
            ("Left Little Intermediate", "Jnt.Hand.Pinky2.L"),
            ("Left Little Distal",       "Jnt.Hand.Pinky3.L"),

            ("Right Thumb Proximal",     "Jnt.Hand.Thumb1.R"),
            ("Right Thumb Intermediate", "Jnt.Hand.Thumb2.R"),
            ("Right Thumb Distal",       "Jnt.Hand.Thumb3.R"),
            ("Right Index Proximal",     "Jnt.Hand.Index1.R"),
            ("Right Index Intermediate", "Jnt.Hand.Index2.R"),
            ("Right Index Distal",       "Jnt.Hand.Index3.R"),
            ("Right Middle Proximal",     "Jnt.Hand.Middle1.R"),
            ("Right Middle Intermediate", "Jnt.Hand.Middle2.R"),
            ("Right Middle Distal",       "Jnt.Hand.Middle3.R"),
            ("Right Ring Proximal",     "Jnt.Hand.Ring1.R"),
            ("Right Ring Intermediate", "Jnt.Hand.Ring2.R"),
            ("Right Ring Distal",       "Jnt.Hand.Ring3.R"),
            ("Right Little Proximal",     "Jnt.Hand.Pinky1.R"),
            ("Right Little Intermediate", "Jnt.Hand.Pinky2.R"),
            ("Right Little Distal",       "Jnt.Hand.Pinky3.R"),
        };

        private static void ApplyHumanoidMapping(ModelImporter importer, string fbxAssetPath)
        {
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(fbxAssetPath);
            if (root == null)
            {
                UnityEngine.Debug.LogWarning("[ConvertAvatar] Cannot read FBX hierarchy for humanoid mapping.");
                return;
            }

            var allTransforms = root.GetComponentsInChildren<Transform>(true);
            var byName = allTransforms
                .GroupBy(t => t.name)
                .ToDictionary(g => g.Key, g => g.First());

            var humanBones = new List<HumanBone>();
            int mapped = 0;
            var missing = new List<string>();

            foreach (var (slot, bone) in HumanoidBoneMap)
            {
                if (!byName.ContainsKey(bone))
                {
                    missing.Add($"{slot} <- {bone}");
                    continue;
                }
                var hb = new HumanBone { humanName = slot, boneName = bone };
                hb.limit.useDefaultValues = true;
                humanBones.Add(hb);
                mapped++;
            }

            // SkeletonBones: every transform in the hierarchy at its bind pose.
            var skeleton = allTransforms.Select(t => new SkeletonBone
            {
                name     = t.name,
                position = t.localPosition,
                rotation = t.localRotation,
                scale    = t.localScale,
            }).ToArray();

            var hd = importer.humanDescription;
            hd.human = humanBones.ToArray();
            hd.skeleton = skeleton;
            hd.armStretch = 0.05f;
            hd.legStretch = 0.05f;
            hd.feetSpacing = 0f;
            hd.upperArmTwist = 0.5f;
            hd.lowerArmTwist = 0.5f;
            hd.upperLegTwist = 0.5f;
            hd.lowerLegTwist = 0.5f;
            hd.hasTranslationDoF = false;
            importer.humanDescription = hd;

            UnityEngine.Debug.Log($"[ConvertAvatar] Humanoid mapping: {mapped} bones mapped.");
            if (missing.Count > 0)
                UnityEngine.Debug.LogWarning($"[ConvertAvatar] Unmapped humanoid slots:\n  " + string.Join("\n  ", missing));
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
                UnityEngine.Debug.Log($"[ConvertAvatar] Marked {marked} textures as normal maps.");
        }

        /// <summary>
        /// Extract embedded FBX materials into a sibling ``_Materials`` folder so
        /// they can be edited / version-controlled / shared. Mirrors the approach
        /// in <c>GlbConverter.ConvertAndImportGlb</c>.
        /// </summary>
        private static void ExtractMaterials(string fbxAssetPath, string glbAssetPath)
        {
            string matAssetDir = Path.ChangeExtension(glbAssetPath, null) + "_Materials";
            string matFullDir = Path.GetFullPath(matAssetDir);
            if (!Directory.Exists(matFullDir))
                Directory.CreateDirectory(matFullDir);

            int extracted = 0;
            var assets = AssetDatabase.LoadAllAssetsAtPath(fbxAssetPath);
            foreach (var asset in assets)
            {
                if (asset is Material mat && !AssetDatabase.IsMainAsset(asset))
                {
                    string matPath = $"{matAssetDir}/{mat.name}.mat";
                    if (File.Exists(Path.GetFullPath(matPath)))
                        continue;

                    string err = AssetDatabase.ExtractAsset(asset, matPath);
                    if (!string.IsNullOrEmpty(err))
                        UnityEngine.Debug.LogWarning($"[ConvertAvatar] Failed to extract material {mat.name}: {err}");
                    else
                        extracted++;
                }
            }

            if (extracted > 0)
                UnityEngine.Debug.Log($"[ConvertAvatar] Extracted {extracted} materials to {matAssetDir}");

            EnableAvatarFaceAlphaClip(matAssetDir);
        }

        /// <summary>
        /// Enable URP alpha clipping on every extracted material whose name
        /// contains "AvatarFace" (the facial sprite materials need alpha test so
        /// the sprite atlas's transparent regions don't show black).
        /// </summary>
        private static void EnableAvatarFaceAlphaClip(string matAssetDir)
        {
            string matFullDir = Path.GetFullPath(matAssetDir);
            if (!Directory.Exists(matFullDir))
                return;

            int patched = 0;
            foreach (string file in Directory.GetFiles(matFullDir, "*.mat"))
            {
                string fileName = Path.GetFileNameWithoutExtension(file);
                if (fileName.IndexOf("AvatarFace", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                string assetPath = matAssetDir + "/" + Path.GetFileName(file);
                var mat = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
                if (mat == null)
                    continue;

                if (mat.HasProperty("_AlphaClip"))
                    mat.SetFloat("_AlphaClip", 1f);
                mat.EnableKeyword("_ALPHATEST_ON");
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;

                EditorUtility.SetDirty(mat);
                patched++;
            }

            if (patched > 0)
            {
                AssetDatabase.SaveAssets();
                UnityEngine.Debug.Log($"[ConvertAvatar] Enabled alpha clipping on {patched} AvatarFace material(s).");
            }
        }
    }
}

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
        [SerializeField] private bool mergeMeshes = true;
        [SerializeField] private bool enforceTpose = true;
        [SerializeField] private List<string> rigidMeshes = new List<string>();

        private DefaultAsset lastScannedGlb;
        private List<string> meshNames = new List<string>();
        private HashSet<string> skinMeshNames = new HashSet<string>();
        private Vector2 rigidScroll;

        [MenuItem("Rec Room Exporter/Convert Avatar")]
        public static void ShowWindow()
        {
            GetWindow<ConvertAvatarWindow>("Convert Avatar");
        }

        private void OnEnable()
        {
            if (string.IsNullOrEmpty(blenderPath))
                blenderPath = AvatarConverter.FindBlenderPath() ?? "";

            if (glbAsset == null)
                glbAsset = FindDefaultAvatarGlb();
        }

        /// <summary>
        /// Look for an asset matching ``Avatar_*.glb`` anywhere in the project and
        /// return it. Used to pre-populate the GLB field when the window opens.
        /// </summary>
        private static DefaultAsset FindDefaultAvatarGlb()
        {
            // AssetDatabase.FindAssets doesn't support full-glob filters, but a
            // name token of "Avatar_" plus t:DefaultAsset narrows the search to
            // candidates we can then filter by extension and prefix.
            string[] guids = AssetDatabase.FindAssets("Avatar_ t:DefaultAsset");
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
                    continue;
                string fileName = Path.GetFileName(path);
                if (!fileName.StartsWith("Avatar_", StringComparison.Ordinal))
                    continue;
                return AssetDatabase.LoadAssetAtPath<DefaultAsset>(path);
            }
            return null;
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
                bool hasWatch = meshNames.Any(
                    n => n.StartsWith("Wrist_Watch_", StringComparison.Ordinal));
                if (hasWatch)
                {
                    EditorGUILayout.Space();
                    watchHand = (WatchHand)EditorGUILayout.EnumPopup(
                        new GUIContent("Watch Hand",
                            "Which wrist the watch should appear on. The Wrist_Watch_* meshes on the " +
                            "unselected side(s) are deleted from the avatar before rigging."),
                        watchHand);
                }

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
                    // Skin meshes drive the body's deformation; binding one rigidly
                    // breaks the entire animation, so they're not eligible.
                    if (skinMeshNames.Contains(name))
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
            mergeMeshes = EditorGUILayout.Toggle(
                new GUIContent("Merge Skinned Meshes",
                    "Join every skinned mesh into a single mesh during conversion so the " +
                    "imported avatar uses one SkinnedMeshRenderer (with multiple submeshes). " +
                    "Recommended for VRChat (the SDK's performance ranking caps Skinned Mesh " +
                    "Renderers at 1) and generally a draw-call win elsewhere. Disable if you " +
                    "need per-region access to the imported meshes."),
                mergeMeshes);

            enforceTpose = EditorGUILayout.Toggle(
                new GUIContent("Enforce T-Pose",
                    "Rotate the rig's rest pose from Rec Room A-pose to T-pose before export. " +
                    "Unity humanoid (and the VRChat SDK) calibrates muscle space relative to " +
                    "T-pose, so leaving this off causes shipped animations (claps, dances, etc.) " +
                    "to drive arms tucked into the torso. Disable only if you specifically need " +
                    "the original A-pose rest preserved."),
                enforceTpose);

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
                    ComputeDeleteMeshes(),
#if HAS_VRCHAT_SDK
                    vrchat: true,
#else
                    vrchat: false,
#endif
                    mergeMeshes: mergeMeshes,
                    enforceTpose: enforceTpose);
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
                EnableStreamingMipmaps(texAssetDir);
            }

            AssetDatabase.ImportAsset(fbxAssetPath, ImportAssetOptions.ForceSynchronousImport);

            var importer = AssetImporter.GetAtPath(fbxAssetPath) as ModelImporter;
            if (importer != null)
            {
                importer.animationType = ModelImporterAnimationType.Human;
                importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                importer.importAnimation = false;
                importer.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;

                // Avoid the "Blendshape Normals: Calculate without Legacy"
                // warning (VRChat flags it as an upload-size issue, and the
                // calculated normals add data we don't need — Rec Room
                // avatars ship without blendshapes).
                importer.importBlendShapeNormals = ModelImporterNormals.None;

#if HAS_VRCHAT_SDK
                // VRChat requires meshes to be CPU-readable so the SDK can
                // inspect/process them at upload time. Outside VRChat projects
                // we leave this off to halve runtime mesh memory.
                importer.isReadable = true;
#endif

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
            skinMeshNames.Clear();

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
                foreach (var (name, isSkin) in ReadGlbMeshNodes(fullPath))
                {
                    meshNames.Add(name);
                    if (isSkin)
                        skinMeshNames.Add(name);
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[ConvertAvatar] Failed to scan GLB '{fullPath}': {ex.Message}");
            }

            // Drop any previously selected names that are no longer present, or
            // that are skin meshes (which are no longer rigid-eligible).
            rigidMeshes.RemoveAll(n => !meshNames.Contains(n) || skinMeshNames.Contains(n));

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
        /// Parse a .glb file's JSON chunk and return every node that references
        /// a mesh, along with whether that mesh uses a "skin" material (one whose
        /// name starts with ``Skin_Mat`` or ``Skin_Gradients_Mat``). The returned
        /// node names match the Blender object names produced by
        /// ``bpy.ops.import_scene.gltf``.
        /// </summary>
        private static IEnumerable<(string Name, bool IsSkin)> ReadGlbMeshNodes(string glbPath)
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

            // Walk three top-level arrays: materials (index -> name), meshes
            // (index -> set of material indices used by its primitives), and
            // nodes (yielding name + mesh index). Combine to flag skin meshes.
            var materialNames = ScanArrayObjects(json, "materials")
                .Select(body => ExtractStringProp(body, "name") ?? "")
                .ToList();

            var meshMaterialIndices = ScanArrayObjects(json, "meshes")
                .Select(CollectPrimitiveMaterialIndices)
                .ToList();

            var results = new List<(string, bool)>();
            int nodeIdx = -1;
            foreach (string body in ScanArrayObjects(json, "nodes"))
            {
                nodeIdx++;
                string name = ExtractStringProp(body, "name");
                if (string.IsNullOrEmpty(name))
                    continue;
                if (!HasNumericProp(body, "mesh"))
                    continue;
                int meshIndex = ExtractIntProp(body, "mesh");
                bool isSkin = false;
                if (meshIndex >= 0 && meshIndex < meshMaterialIndices.Count)
                {
                    foreach (int matIdx in meshMaterialIndices[meshIndex])
                    {
                        if (matIdx < 0 || matIdx >= materialNames.Count)
                            continue;
                        if (IsSkinMaterialName(materialNames[matIdx]))
                        {
                            isSkin = true;
                            break;
                        }
                    }
                }
                results.Add((name, isSkin));
            }
            return results;
        }

        private static bool IsSkinMaterialName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.StartsWith("Skin_Mat", StringComparison.Ordinal) ||
                   name.StartsWith("Skin_Gradients_Mat", StringComparison.Ordinal);
        }

        /// <summary>
        /// Yield the JSON text of each top-level object inside the array named
        /// ``key`` at the document root. Returns nothing if the array is absent.
        /// </summary>
        private static IEnumerable<string> ScanArrayObjects(string json, string key)
        {
            int start = FindArrayStart(json, key);
            if (start < 0) yield break;

            int i = start + 1; // skip '['
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
                            yield return json.Substring(objStart, i - objStart + 1);
                            objStart = -1;
                        }
                    }
                }
                else if (c == '[') depth++;
                else if (c == ']') depth--;
                i++;
            }
        }

        /// <summary>
        /// Extract every ``"material": &lt;int&gt;`` reference from the
        /// ``primitives`` array inside a mesh object body.
        /// </summary>
        private static List<int> CollectPrimitiveMaterialIndices(string meshBody)
        {
            var list = new List<int>();
            // Locate the primitives array within this mesh object.
            int primStart = FindArrayStart(meshBody, "primitives");
            if (primStart < 0) return list;

            string primSection = meshBody.Substring(primStart);
            foreach (string primBody in ScanArrayObjectsFromArrayStart(primSection))
            {
                if (HasNumericProp(primBody, "material"))
                    list.Add(ExtractIntProp(primBody, "material"));
            }
            return list;
        }

        private static IEnumerable<string> ScanArrayObjectsFromArrayStart(string arraySection)
        {
            // arraySection starts with '['. Reuse the same scanner pattern.
            int i = 1;
            int depth = 1;
            int objStart = -1;
            int objDepth = 0;
            bool inString = false;
            bool escape = false;
            while (i < arraySection.Length && depth > 0)
            {
                char c = arraySection[i];
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
                            yield return arraySection.Substring(objStart, i - objStart + 1);
                            objStart = -1;
                        }
                    }
                }
                else if (c == '[') depth++;
                else if (c == ']') depth--;
                i++;
            }
        }

        private static int ExtractIntProp(string body, string key)
        {
            string token = "\"" + key + "\"";
            int idx = body.IndexOf(token, StringComparison.Ordinal);
            if (idx < 0) return -1;
            int p = idx + token.Length;
            while (p < body.Length && char.IsWhiteSpace(body[p])) p++;
            if (p >= body.Length || body[p] != ':') return -1;
            p++;
            while (p < body.Length && char.IsWhiteSpace(body[p])) p++;
            int sign = 1;
            if (p < body.Length && body[p] == '-') { sign = -1; p++; }
            int value = 0;
            bool any = false;
            while (p < body.Length && char.IsDigit(body[p]))
            {
                value = value * 10 + (body[p] - '0');
                p++;
                any = true;
            }
            return any ? sign * value : -1;
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
        /// Enable ``streamingMipmaps`` and switch the mipmap filter to
        /// ``Kaiser`` on every mipmapped texture in <paramref name="texAssetDir"/>.
        /// Suppresses the VRChat "mipmapped textures without Streaming Mip Maps"
        /// warning (and the "Box mipmap filtering blurs distant textures" hint),
        /// and is a no-op in projects that don't have Texture Streaming enabled
        /// in Quality Settings.
        /// </summary>
        private static void EnableStreamingMipmaps(string texAssetDir)
        {
            string fullDir = Path.GetFullPath(texAssetDir);
            if (!Directory.Exists(fullDir))
                return;

            string[] extensions = { "*.png", "*.jpg", "*.tga" };
            int updated = 0;
            foreach (string ext in extensions)
            {
                foreach (string file in Directory.GetFiles(fullDir, ext))
                {
                    string assetPath = texAssetDir + "/" + Path.GetFileName(file);
                    var texImporter = AssetImporter.GetAtPath(assetPath) as TextureImporter;
                    if (texImporter == null) continue;
                    if (!texImporter.mipmapEnabled) continue;
                    bool changed = false;
                    if (!texImporter.streamingMipmaps)
                    {
                        texImporter.streamingMipmaps = true;
                        changed = true;
                    }
                    if (texImporter.mipmapFilter != TextureImporterMipFilter.KaiserFilter)
                    {
                        texImporter.mipmapFilter = TextureImporterMipFilter.KaiserFilter;
                        changed = true;
                    }
                    if (!changed) continue;
                    texImporter.SaveAndReimport();
                    updated++;
                }
            }

            if (updated > 0)
                UnityEngine.Debug.Log($"[ConvertAvatar] Updated mipmap settings (streaming + Kaiser filter) on {updated} textures.");
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

                ApplyAlphaClip(mat);

                EditorUtility.SetDirty(mat);
                patched++;
            }

            if (patched > 0)
            {
                AssetDatabase.SaveAssets();
                UnityEngine.Debug.Log($"[ConvertAvatar] Enabled alpha clipping on {patched} AvatarFace material(s).");
            }
        }

        /// <summary>
        /// Switch <paramref name="mat"/> into alpha-test (cutout) mode. Handles
        /// both Built-in Render Pipeline (Standard shader: ``_Mode``, blend
        /// states, ``_Cutoff``) and URP/HDRP Lit shaders (``_AlphaClip`` +
        /// ``_Surface``). The ``_ALPHATEST_ON`` keyword is shared.
        /// </summary>
        private static void ApplyAlphaClip(Material mat)
        {
            // URP / HDRP Lit: a single boolean property toggles alpha clipping.
            if (mat.HasProperty("_AlphaClip"))
                mat.SetFloat("_AlphaClip", 1f);
            // URP also exposes a Surface enum (0 = Opaque, 1 = Transparent);
            // alpha clip lives under Opaque, so leave it untouched.

            // Built-in Standard shader: ``_Mode`` is an enum (0 Opaque, 1 Cutout,
            // 2 Fade, 3 Transparent). Cutout requires opaque blend states +
            // ZWrite + the alpha-test keyword + an AlphaTest queue.
            if (mat.HasProperty("_Mode"))
            {
                mat.SetFloat("_Mode", 1f); // Cutout
                if (mat.HasProperty("_SrcBlend"))
                    mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
                if (mat.HasProperty("_DstBlend"))
                    mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.Zero);
                if (mat.HasProperty("_ZWrite"))
                    mat.SetFloat("_ZWrite", 1f);
                mat.DisableKeyword("_ALPHABLEND_ON");
                mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            }

            if (mat.HasProperty("_Cutoff"))
                mat.SetFloat("_Cutoff", 0.5f);

            mat.EnableKeyword("_ALPHATEST_ON");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace RRSceneExporter.RRAvatar
{
    public class ConvertAvatarWindow : EditorWindow
    {
        [SerializeField] private string blenderPath;
        [SerializeField] private DefaultAsset glbAsset;

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
                ok = AvatarConverter.ConvertGlbToRiggedFbx(blenderPath, glbFullPath, fbxFullPath);
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

            ("LeftThumbProximal",     "Jnt.Hand.Thumb1.L"),
            ("LeftThumbIntermediate", "Jnt.Hand.Thumb2.L"),
            ("LeftThumbDistal",       "Jnt.Hand.Thumb3.L"),
            ("LeftIndexProximal",     "Jnt.Hand.Index1.L"),
            ("LeftIndexIntermediate", "Jnt.Hand.Index2.L"),
            ("LeftIndexDistal",       "Jnt.Hand.Index3.L"),
            ("LeftMiddleProximal",     "Jnt.Hand.Middle1.L"),
            ("LeftMiddleIntermediate", "Jnt.Hand.Middle2.L"),
            ("LeftMiddleDistal",       "Jnt.Hand.Middle3.L"),
            ("LeftRingProximal",     "Jnt.Hand.Ring1.L"),
            ("LeftRingIntermediate", "Jnt.Hand.Ring2.L"),
            ("LeftRingDistal",       "Jnt.Hand.Ring3.L"),
            ("LeftLittleProximal",     "Jnt.Hand.Pinky1.L"),
            ("LeftLittleIntermediate", "Jnt.Hand.Pinky2.L"),
            ("LeftLittleDistal",       "Jnt.Hand.Pinky3.L"),

            ("RightThumbProximal",     "Jnt.Hand.Thumb1.R"),
            ("RightThumbIntermediate", "Jnt.Hand.Thumb2.R"),
            ("RightThumbDistal",       "Jnt.Hand.Thumb3.R"),
            ("RightIndexProximal",     "Jnt.Hand.Index1.R"),
            ("RightIndexIntermediate", "Jnt.Hand.Index2.R"),
            ("RightIndexDistal",       "Jnt.Hand.Index3.R"),
            ("RightMiddleProximal",     "Jnt.Hand.Middle1.R"),
            ("RightMiddleIntermediate", "Jnt.Hand.Middle2.R"),
            ("RightMiddleDistal",       "Jnt.Hand.Middle3.R"),
            ("RightRingProximal",     "Jnt.Hand.Ring1.R"),
            ("RightRingIntermediate", "Jnt.Hand.Ring2.R"),
            ("RightRingDistal",       "Jnt.Hand.Ring3.R"),
            ("RightLittleProximal",     "Jnt.Hand.Pinky1.R"),
            ("RightLittleIntermediate", "Jnt.Hand.Pinky2.R"),
            ("RightLittleDistal",       "Jnt.Hand.Pinky3.R"),
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

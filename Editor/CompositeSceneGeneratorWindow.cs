using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using RecRoom.Core.Studio;
using RecRoom.Protobuf;
using Debug = UnityEngine.Debug;

namespace CompositeSceneGenerator
{
    public class CompositeSceneGeneratorWindow : EditorWindow
    {
        [SerializeField] private DefaultAsset makerPenGlb;
        [SerializeField] private SceneAsset baseScene;
        [SerializeField] private DefaultAsset roomBinpb;
        [SerializeField] private SceneAsset recRoomObjectsScene;
        [SerializeField] private RecRoomBuiltInObjectData builtInRegistry;
        [SerializeField] private string blenderPath;
        [SerializeField] private List<GameObject> hiddenPrefabs = new List<GameObject>();

        private Vector2 scrollPos;

        private static bool ProtosExist()
        {
            string fullDir = Path.GetFullPath("Assets/RecRoomCache/Generated");
            return Directory.Exists(fullDir)
                && Directory.GetFiles(fullDir, "*.cs", SearchOption.AllDirectories).Length > 0;
        }

        [MenuItem("Rec Room/Generate Composite Scene", true)]
        private static bool ValidateShowWindow()
        {
            return ProtosExist();
        }

        [MenuItem("Rec Room/Generate Composite Scene")]
        public static void ShowWindow()
        {
            GetWindow<CompositeSceneGeneratorWindow>("Composite Scene Generator");
        }

        private void OnEnable()
        {
            if (builtInRegistry == null)
            {
                string[] guids = AssetDatabase.FindAssets("t:RecRoomBuiltInObjectData");
                if (guids.Length > 0)
                    builtInRegistry = AssetDatabase.LoadAssetAtPath<RecRoomBuiltInObjectData>(AssetDatabase.GUIDToAssetPath(guids[0]));
            }
        }

        private void OnGUI()
        {
            if (!ProtosExist())
            {
                EditorGUILayout.HelpBox(
                    "Protobuf classes are missing. Use Rec Room > Generate Protobuf Classes to generate them first.",
                    MessageType.Error);
                return;
            }

            scrollPos = EditorGUILayout.BeginScrollView(scrollPos);

            EditorGUILayout.LabelField("Rec Room Composite Scene Generator", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            // --- Setup ---
            EditorGUILayout.LabelField("Setup", EditorStyles.boldLabel);

            builtInRegistry = (RecRoomBuiltInObjectData)EditorGUILayout.ObjectField(
                new GUIContent("Built-In Asset Registry", "The Rec Room Studio asset registry that maps prefab GUIDs to built-in object prefabs."),
                builtInRegistry, typeof(RecRoomBuiltInObjectData), false);

            if (string.IsNullOrEmpty(blenderPath))
                blenderPath = BlenderConverter.FindBlenderPath() ?? "";

            EditorGUILayout.BeginHorizontal();
            blenderPath = EditorGUILayout.TextField(
                new GUIContent("Blender Path", "Path to blender.exe. Auto-detected from common install locations. Required for GLB to FBX conversion."),
                blenderPath);
            if (GUILayout.Button("Browse", GUILayout.Width(60)))
            {
                string selected = EditorUtility.OpenFilePanel("Select Blender Executable", "", "exe");
                if (!string.IsNullOrEmpty(selected))
                    blenderPath = selected;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();

            // --- Room Exporter ---
            EditorGUILayout.LabelField("Room Exporter", EditorStyles.boldLabel);

            makerPenGlb = (DefaultAsset)EditorGUILayout.ObjectField(
                new GUIContent("Maker Pen GLB File", "The raw .glb exported from Rec Room containing maker pen geometry. Converted to FBX via Blender during generation. Should have a unique name if running multiple rooms."),
                makerPenGlb, typeof(DefaultAsset), false);

            roomBinpb = (DefaultAsset)EditorGUILayout.ObjectField(
                new GUIContent("Room .binpb File", "The persisted room data protobuf file containing all placed object transforms and prefab references."),
                roomBinpb, typeof(DefaultAsset), false);

            EditorGUILayout.Space();

            // --- Studio (optional) ---
            EditorGUILayout.LabelField("Studio (optional)", EditorStyles.boldLabel);

            baseScene = (SceneAsset)EditorGUILayout.ObjectField(
                new GUIContent("Base Unity Scene", "Optional scene for Rec Room Studio rooms. The composite scene will be a copy of this with objects added. Leave empty for a default scene."),
                baseScene, typeof(SceneAsset), false);

            recRoomObjectsScene = (SceneAsset)EditorGUILayout.ObjectField(
                new GUIContent("RecRoomObjects Scene", "The RecRoomObjects runtime data scene for this subroom (e.g. RecCenter-Main-RecRoomObjects). Contains Studio Object prefab ID mappings."),
                recRoomObjectsScene, typeof(SceneAsset), false);

            EditorGUILayout.Space();

            // Validation
            bool valid = true;
            if (roomBinpb == null) { EditorGUILayout.HelpBox("Room .binpb file is required.", MessageType.Warning); valid = false; }
            if (builtInRegistry == null) { EditorGUILayout.HelpBox("Built-in asset registry is required.", MessageType.Warning); valid = false; }

            if (makerPenGlb != null)
            {
                string glbAssetPath = AssetDatabase.GetAssetPath(makerPenGlb);
                if (!glbAssetPath.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) &&
                    !glbAssetPath.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase))
                {
                    EditorGUILayout.HelpBox("Maker pen file should be a .glb or .gltf file.", MessageType.Warning);
                    valid = false;
                }
                else if (string.IsNullOrEmpty(blenderPath) || !File.Exists(blenderPath))
                {
                    EditorGUILayout.HelpBox(
                        "Blender is required to convert GLB files. Install Blender and set the path above.",
                        MessageType.Warning);
                    EditorGUILayout.BeginHorizontal();
                    if (GUILayout.Button("Download Blender"))
                        Application.OpenURL("https://www.blender.org/download/");
                    if (GUILayout.Button("Check for installation"))
                        blenderPath = BlenderConverter.FindBlenderPath() ?? "";
                    EditorGUILayout.EndHorizontal();
                    valid = false;
                }
            }

            if (roomBinpb != null)
            {
                string path = AssetDatabase.GetAssetPath(roomBinpb);
                if (!path.EndsWith(".binpb", StringComparison.OrdinalIgnoreCase))
                {
                    EditorGUILayout.HelpBox("Room file should be a .binpb file.", MessageType.Warning);
                    valid = false;
                }
            }

            EditorGUI.BeginDisabledGroup(!valid);
            if (GUILayout.Button("Generate Composite Scene", GUILayout.Height(30)))
            {
                GenerateScene();
            }
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.Space();

            EditorGUI.BeginDisabledGroup(EditorSceneManager.GetActiveScene().path == null ||
                string.IsNullOrEmpty(EditorSceneManager.GetActiveScene().path));
            if (GUILayout.Button("Export Scene as .unitypackage", GUILayout.Height(25)))
            {
                ExportScenePackage();
            }
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndScrollView();
        }

        private void ExportScenePackage()
        {
            string scenePath = EditorSceneManager.GetActiveScene().path;
            if (string.IsNullOrEmpty(scenePath))
            {
                EditorUtility.DisplayDialog("Export", "No scene is currently open or the scene has not been saved.", "OK");
                return;
            }

            string defaultName = Path.GetFileNameWithoutExtension(scenePath) + ".unitypackage";
            string exportPath = EditorUtility.SaveFilePanel("Export Scene Package", "", defaultName, "unitypackage");
            if (string.IsNullOrEmpty(exportPath))
                return;

            string[] allDeps = AssetDatabase.GetDependencies(scenePath, true);
            var assetDeps = new List<string>();
            foreach (string dep in allDeps)
            {
                if (!dep.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                    assetDeps.Add(dep);
            }

            // Include the shader log sidecar if it exists
            string sceneName = Path.GetFileNameWithoutExtension(scenePath);
            string shaderLogPath = "Assets/RecRoomCache/ShaderLog/" + sceneName + ".json";
            if (File.Exists(Path.GetFullPath(shaderLogPath)) && !assetDeps.Contains(shaderLogPath))
                assetDeps.Add(shaderLogPath);

            AssetDatabase.ExportPackage(assetDeps.ToArray(), exportPath, ExportPackageOptions.Default);
            Debug.Log($"[CompositeScene] Exported {assetDeps.Count} assets to: {exportPath} (excluded {allDeps.Length - assetDeps.Count} package dependencies)");
        }

        private void GenerateScene()
        {
            try
            {
                // 1. Deserialize room protobuf early so we can use metadata for naming
                EditorUtility.DisplayProgressBar("Generating Composite Scene", "Deserializing room data...", 0.02f);
                string binpbPath = AssetDatabase.GetAssetPath(roomBinpb);
                string fullBinpbPath = Path.GetFullPath(binpbPath);
                byte[] roomData = File.ReadAllBytes(fullBinpbPath);

                var persistedRoom = PersistedRoomData.Parser.ParseFrom(roomData);
                int viewCount = persistedRoom.PersistenceViews.Count;
                Debug.Log($"[CompositeScene] Deserialized room with {viewCount} persistence views.");

                string subRoomLabel = persistedRoom.SubRoomId != 0
                    ? persistedRoom.SubRoomId.ToString()
                    : null;

                // 2. Prompt for save location
                string defaultName;
                string defaultDir;
                if (baseScene != null)
                {
                    defaultName = Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(baseScene)) + "_Composite.unity";
                    defaultDir = Path.GetDirectoryName(AssetDatabase.GetAssetPath(baseScene));
                }
                else
                {
                    defaultName = (subRoomLabel ?? "Composite") + "_Composite.unity";
                    defaultDir = "Assets";
                }
                string savePath = EditorUtility.SaveFilePanel(
                    "Save Composite Scene", defaultDir, defaultName, "unity");
                if (string.IsNullOrEmpty(savePath))
                    return;

                if (savePath.StartsWith(Application.dataPath))
                    savePath = "Assets" + savePath.Substring(Application.dataPath.Length);

                EditorUtility.DisplayProgressBar("Generating Composite Scene", "Setting up scene...", 0.05f);

                // 3. Create or clone the scene
                Scene scene;
                if (baseScene != null)
                {
                    AssetDatabase.CopyAsset(AssetDatabase.GetAssetPath(baseScene), savePath);
                    scene = EditorSceneManager.OpenScene(savePath, OpenSceneMode.Single);
                }
                else
                {
                    scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                    EditorSceneManager.SaveScene(scene, savePath);
                }

                // Reset skybox to Unity default
                RenderSettings.skybox = null;

                // 4. Convert GLB and place Maker Pen asset at origin
                string sceneName = Path.GetFileNameWithoutExtension(savePath);
                if (makerPenGlb != null)
                {
                    EditorUtility.DisplayProgressBar("Generating Composite Scene", "Converting GLB to FBX via Blender...", 0.08f);
                    GameObject makerPenAsset = GlbConverter.ConvertAndImportGlb(makerPenGlb, blenderPath, sceneName);
                    if (makerPenAsset != null)
                    {
                        EditorUtility.DisplayProgressBar("Generating Composite Scene", "Adding Maker Pen geometry...", 0.1f);
                        var makerPenRoot = new GameObject("MakerPen");
                        var instance = (GameObject)PrefabUtility.InstantiatePrefab(makerPenAsset, scene);
                        if (instance != null)
                        {
                            instance.transform.SetParent(makerPenRoot.transform, false);
                            instance.transform.localPosition = Vector3.zero;
                        }
                        else
                        {
                            instance = Instantiate(makerPenAsset);
                            instance.name = makerPenAsset.name;
                            instance.transform.SetParent(makerPenRoot.transform, false);
                            instance.transform.localPosition = Vector3.zero;
                        }

                        // Assign colliders to shape meshes based on physics mode from room data
                        EditorUtility.DisplayProgressBar("Generating Composite Scene", "Assigning shape colliders...", 0.15f);
                        ShapeColliderAssigner.AssignColliders(makerPenRoot, persistedRoom);
                    }
                }

                // 5. Build prefab lookup from registry
                EditorUtility.DisplayProgressBar("Generating Composite Scene", "Building prefab lookup...", 0.3f);
                var prefabLookup = PrefabResolver.BuildPrefabLookup(builtInRegistry);

                if (recRoomObjectsScene != null)
                    PrefabResolver.AddStudioObjectPrefabs(prefabLookup, AssetDatabase.GetAssetPath(recRoomObjectsScene));

                var usedGuids = DependencyCache.CollectUsedPrefabGuids(persistedRoom);
                DependencyCache.CachePackageDependencies(prefabLookup, usedGuids);

                // Add base components to cached prefab assets (e.g. Light on light prefabs)
                PrefabPostProcessorRegistry.PreparePrefabs(prefabLookup);

                // 6. Instantiate objects using the connectable graph for hierarchy
                var objectRoot = new GameObject("RecRoomObjects");
                int placed = 0;
                int skipped = 0;

                var viewById = new Dictionary<string, PersistenceViewData>();
                foreach (var view in persistedRoom.PersistenceViews)
                {
                    if (view.Id != null && !view.Id.IsEmpty)
                    {
                        string key = view.Id.ToBase64();
                        if (!viewById.ContainsKey(key))
                            viewById[key] = view;
                    }
                }

                var placedViewIds = new HashSet<string>();
                if (persistedRoom.ConnectableGraphData?.RootNode != null)
                {
                    ObjectPlacer.PlaceConnectableNode(
                        persistedRoom.ConnectableGraphData.RootNode,
                        objectRoot.transform,
                        viewById, prefabLookup, scene,
                        placedViewIds, ref placed, ref skipped);
                }

                for (int i = 0; i < viewCount; i++)
                {
                    if (i % 100 == 0)
                    {
                        float progress = 0.3f + 0.65f * (i / (float)viewCount);
                        EditorUtility.DisplayProgressBar("Generating Composite Scene",
                            $"Placing objects... ({i}/{viewCount})", progress);
                    }

                    var view = persistedRoom.PersistenceViews[i];
                    if (view.Id != null && !view.Id.IsEmpty && placedViewIds.Contains(view.Id.ToBase64()))
                        continue;

                    ObjectPlacer.PlaceView(view, objectRoot.transform,
                        prefabLookup, scene, ref placed, ref skipped);
                }

                // 7. Save the scene
                EditorUtility.DisplayProgressBar("Generating Composite Scene", "Saving scene...", 0.97f);
                EditorSceneManager.SaveScene(scene);

                // 8. Write shader log sidecar for cross-pipeline conversion
                ShaderRemapper.WriteShaderLog(savePath);

                Debug.Log($"[CompositeScene] Complete! Placed {placed} objects, skipped {skipped}. Scene saved to: {savePath}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CompositeScene] Error: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }
    }
}

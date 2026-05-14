# RR Scene Exporter

A Unity editor package that generates standalone, dependency-free Unity scenes from Rec Room data exports. It deserializes room protobuf data, converts maker pen GLB geometry to FBX via Blender, resolves and caches all prefab and material dependencies from Rec Room Studio packages, and places every object at its correct transform. The result is a self-contained `.unitypackage` that can be imported into any Unity project — including VRChat worlds — without needing Rec Room Studio installed.

## Installation

Install the package in your Unity project using one of these methods:

### Option A: Git URL (requires [Git](https://git-scm.com/) installed)

1. In Unity, open **Window > Package Manager**.
2. Click the **+** button in the top-left and select **Add package from git URL...**
3. Enter `https://github.com/willchil/rr-scene-exporter.git` and click **Add**.

### Option B: Manual download (no Git required)

1. Go to the [GitHub repository](https://github.com/willchil/rr-scene-exporter) and click **Code > Download ZIP**.
2. Extract the ZIP to a permanent location on your computer.
3. In Unity, open **Window > Package Manager**.
4. Click the **+** button in the top-left and select **Add package from disk...**
5. Navigate to the extracted folder and select the `package.json` file.

## Dependencies

### Rec Room Studio

The scene generator runs inside a **Rec Room Studio** Unity project. You need:

- A Rec Room Studio project (Unity 6) with the `com.recroom.studio.common` package installed. This provides the built-in object prefab registry and all the asset packages the room references.
- A **Rec Room data export** of the room you want to convert. The export contains:
  - `persisted_room_data.binpb` — the serialized room layout (object positions, rotations, scales, prefab GUIDs, hierarchy)
  - `Scene.glb` — maker pen geometry (shapes drawn by players)
  - `descriptor_set.binpb` — protobuf schema definitions needed to deserialize the room data

The data export is produced by the final version of the Steam client, and is not part of this package. [See video tutorial](https://www.youtube.com/watch?v=IjsTkMwXR1w)

### Blender

[Blender](https://www.blender.org/download/) (3.0+) is required to convert the `.glb` maker pen geometry into `.fbx` format that Unity can import with proper materials. The package auto-detects common install locations (Program Files, Steam, PATH). If auto-detection fails, you can set the path manually in the editor window.

### protoc (Protocol Buffer Compiler)

[protoc](https://github.com/protocolbuffers/protobuf/releases) is needed once to generate C# classes from the protobuf descriptor set. Install it via:

- `winget install protobuf` (Windows)
- Download from the [protobuf releases page](https://github.com/protocolbuffers/protobuf/releases)

After generating the protobuf classes, protoc is no longer needed.

## Exporting an Avatar

The avatar converter takes a `.glb` exported from the Rec Room game client and produces a rigged Unity humanoid `.fbx` ready to drop into a scene. If the **VRChat SDK** is detected in the project, it additionally wraps the avatar in a prefab with a `VRCAvatarDescriptor` and instantiates it into the open scene so it's immediately uploadable as a VRChat avatar.

### 1. Export the avatar from Rec Room

In the Rec Room game client, export your avatar **as an A-pose**. The converter expects the rest pose to be the Rec Room A-pose; T-pose or other rest poses will not rig correctly.

### 2. Convert the avatar

1. Open **Rec Room Exporter > Convert Avatar**.
2. Set the **Avatar GLB (A-Pose)** to the `.glb` exported from the game client.
3. Click **Convert Avatar**.

The converter will run Blender in the background to rig the mesh, optionally rotate the rest pose to T-pose, and write a Unity-humanoid `.fbx` next to the source `.glb`. In a VRChat project the resulting prefab is also instantiated into the active scene.

### Additional settings

The remaining fields in the window generally don't need to be adjusted — the defaults handle every Rec Room avatar variant the converter supports:

- **Watch Hand** — picks which wrist the watch goes on (off-hand watches are deleted). Appears only on full body avatars, as bean avatars do not include watches
- **Rigid Meshes** — toggle on individual items if you'd like them to remain as rigid objects and not bend with the avatar. Turn everything on for the classic bean look.
- **Merge Skinned Meshes** *(on)* — joins every skinned mesh into one renderer to improve performance in realtime applications, such as VRChat.
- **Enforce T-Pose** *(on)* — rotates the rest pose to T-pose so Unity humanoid muscle space (and the VRChat SDK) calibrates correctly.

## Exporting a Room from Rec Room Studio

### 1. Install the package

Follow the [Installation](#installation) instructions above to add the package to your Rec Room Studio project.

### 2. Generate protobuf classes

1. Copy the `descriptor_set.binpb` file from your data export into your project (e.g. into an `Assets/RoomExport/` folder).
2. Open **Rec Room Exporter > Generate Protobuf Classes**.
3. Assign the `descriptor_set.binpb` file and set the `protoc` path if it wasn't auto-detected.
4. Click **Generate**. Unity will compile the generated C# classes, which will be used when generating your scenes.

You only need to do this once per project.

### 3. Generate the composite scene

1. Copy the subroom files from your data export into the project:
   - `persisted_room_data.binpb` (required) — the room layout data
   - `Scene.glb` — maker pen geometry
2. Open **Rec Room Exporter > Generate Composite Scene**.
3. Fill in the fields:
   - **Built-In Asset Registry** — auto-populated from the Rec Room Studio package. This maps prefab GUIDs to the actual prefab assets.
   - **Blender Path** — auto-detected or set manually.
   - **Maker Pen GLB File** — the `Scene.glb` from the data export.
   - **Room .binpb File** — the `persisted_room_data.binpb` from the data export.
   - **Base Unity Scene** (optional) — for Studio rooms, assign the subroom's base scene (e.g. `RecCenter-Main.unity`). The composite scene will be a copy of this with all objects added on top. Leave empty for a blank scene.
   - **RecRoomObjects Scene** (optional) — the `RecRoomObjects` runtime data 'orange' scene for the subroom. This contains Studio Object prefab ID → prefab mappings that aren't in the built-in registry.
4. Click **Generate Composite Scene** and choose a save location.

The generator will:
- Deserialize the protobuf room data
- Convert the GLB to FBX via Blender (cached for subsequent runs)
- Resolve every prefab GUID to its Rec Room Studio prefab
- Cache all referenced package assets (prefabs, meshes, textures, materials) into `Assets/RecRoomCache/`
- Strip Rec Room scripts from cached prefabs
- Remap Rec Room's custom shaders to standard URP equivalents
- Instantiate every object at its recorded position, rotation, and scale
- Write a shader log file for later cross-pipeline conversion

### 4. Export as .unitypackage

1. With the generated scene open, click **Export Scene as .unitypackage** in the generator window.
2. Choose a save location.

The export automatically collects all scene dependencies (meshes, textures, materials, prefabs) while excluding anything under `Packages/`. It also includes the shader log file needed for VRChat import.

## Importing into a VRChat Project

### 1. Install the package

Follow the [Installation](#installation) instructions above to add the package to your VRChat project.

The VRChat-specific tools compile automatically when the VRChat SDK (`com.vrchat.base`) is detected — no configuration needed.

### 2. Import the .unitypackage

1. In your VRChat project, go to **Assets > Import Package > Custom Package** and select the `.unitypackage` exported from Rec Room Studio.
2. Import all assets. The package will automatically strip any missing script references from the cached prefabs on import.

### 3. Convert materials

VRChat uses the Built-in render pipeline, but the exported materials use URP shaders. To convert them:

1. Open the imported scene.
2. Go to **Rec Room Exporter > Convert materials**.

This reads the shader log file to determine what each material's original shader was, then remaps them to Built-in pipeline equivalents:
- Lit materials → `Standard` shader (with correct albedo, normal maps, metallic, smoothness, and emission)
- Unlit materials → `Unlit/Texture` shader
- Transparency, alpha clipping, and additive blending modes are preserved.

Material properties are read from serialized data rather than the Material API, so they're recovered correctly even though the URP shaders aren't available in the Built-in pipeline.

### 4. Set up your VRChat world

The imported scene contains:
- A **MakerPen** root object with the converted FBX geometry
- A **RecRoomObjects** root object with all placed prefab instances in their original hierarchy

From here you can add VRChat components (VRC Scene Descriptor, spawn points, etc.) and publish as a world.

## Supported Features

- Import maker pen GLB geometry with textures and colors
- Import props (and hide ones that are only visible on the circuit layer)
- Copy studio objects
- Assign collision to shapes and objects
- Add rigidbodies to physical objects
- Convert Rec Room lights into Unity lights
- Remap shaders to URP or the Built-in pipeline
- Export the scene as a Unity package with no Rec Room Studio dependencies

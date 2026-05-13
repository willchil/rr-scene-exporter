"""
Blender headless script: Convert a Rec Room avatar GLB into a rigged FBX.

Workflow (the .blend opened by Blender must be the bundled rigged_reference.blend,
which contains Avatar_Skeleton, BodyMesh_LOD0 and the Wrist_Watch_*_LOD0
source meshes used as weight donors):

    1. Import the provided .glb. It contributes an "AvatarRoot" empty with
       mesh children that visually overlap the FB body (after AvatarRoot is
       reset to identity world transform).
    2. For each mesh under AvatarRoot, transfer vertex weights from the
       matching FB source mesh (nearest-face polygon interpolation in world
       space), then add an Armature modifier targeting Avatar_Skeleton.
    3. Export AvatarRoot + its mesh children + Avatar_Skeleton to FBX with
       Unity-friendly settings.

Usage:
    blender rigged_reference.blend --background --python avatar_convert.py -- input.glb output.fbx
"""

import bpy
import os
import re
import sys

from mathutils import Matrix, Vector


# Blender suffixes imported objects with ``.001``, ``.002``, ... when an object
# with the same name already exists in the file (rigged_reference.blend pre-defines
# Wrist_Watch_*_LOD0 as weight donors, so the GLB's identically-named meshes
# get renamed). Strip that suffix when matching against caller-supplied names.
_DUP_SUFFIX = re.compile(r"\.\d{3}$")


def base_name(name):
    return _DUP_SUFFIX.sub("", name)


# ---------------------------------------------------------------------------
# Argument parsing
# ---------------------------------------------------------------------------

def parse_args():
    if "--" in sys.argv:
        args = sys.argv[sys.argv.index("--") + 1:]
        if len(args) >= 2:
            # Trailing args are mesh names: bare names mark rigid binds; names
            # after a ``--delete`` marker are removed from the avatar before
            # rigging. ``--vrchat`` is a standalone toggle (it has no value)
            # that opts into VRChat-specific rig adjustments.
            rigid = []
            delete = []
            vrchat = False
            bucket = rigid
            for a in args[2:]:
                if a == "--delete":
                    bucket = delete
                    continue
                if a == "--vrchat":
                    vrchat = True
                    continue
                if a:
                    bucket.append(a)
            return args[0], args[1], rigid, delete, vrchat
    raise RuntimeError(
        "Usage: blender rigged_reference.blend --background --python avatar_convert.py "
        "-- input.glb output.fbx [rigid_mesh_name ...] [--delete mesh_name ...] [--vrchat]"
    )


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def select_only(*objs):
    bpy.ops.object.mode_set(mode='OBJECT')
    bpy.ops.object.select_all(action='DESELECT')
    for o in objs:
        if o is not None:
            o.select_set(True)
    if objs and objs[0] is not None:
        bpy.context.view_layer.objects.active = objs[0]


def pick_weight_source(target_name):
    """Return the FB mesh whose weights should be transferred onto ``target_name``."""
    if "Wrist_Watch_L" in target_name:
        return bpy.data.objects.get("Wrist_Watch_L_LOD0")
    if "Wrist_Watch_R" in target_name:
        return bpy.data.objects.get("Wrist_Watch_R_LOD0")
    # Everything else (body, head, hands, accessories) takes weights from the body.
    return bpy.data.objects.get("BodyMesh_LOD0")


def transfer_weights(target, source):
    select_only(target, source)
    bpy.context.view_layer.objects.active = source
    for vg in list(target.vertex_groups):
        target.vertex_groups.remove(vg)
    bpy.ops.object.data_transfer(
        use_reverse_transfer=False,
        data_type='VGROUP_WEIGHTS',
        use_create=True,
        vert_mapping='POLYINTERP_NEAREST',
        use_object_transform=True,
        layers_select_src='ALL',
        layers_select_dst='NAME',
        mix_mode='REPLACE',
    )

    # Re-normalise so each vertex's weights sum to 1.0 (cleanup after the
    # interpolated transfer, which can leave fractional totals).
    select_only(target)
    if target.vertex_groups:
        bpy.ops.object.vertex_group_normalize_all(lock_active=False)


def rename_skin_meshes(avatar_root):
    """Rename any GLB mesh whose materials identify it as the avatar's base
    skin (material name starts with ``Skin_Mat`` or ``Skin_Gradients_Mat``) to
    ``Skin``. Blender will auto-suffix collisions (``Skin.001`` etc.).
    """
    for child in list(avatar_root.children):
        if child.type != 'MESH' or child.data is None:
            continue
        for mat in child.data.materials:
            if mat is None:
                continue
            n = mat.name
            if n.startswith("Skin_Mat") or n.startswith("Skin_Gradients_Mat"):
                old = child.name
                child.name = "Skin"
                print(f"Renamed skin mesh: {old} -> {child.name}")
                break


def fix_spine_hierarchy(armature):
    """Re-parent the shoulder and neck bones to ``Jnt.Spine.Chest``.

    Unity humanoid (and the VRChat SDK validator in particular) requires that
    both shoulders and the neck share the chest as their direct parent in the
    bone hierarchy. The source rig parents them elsewhere, so we fix it here
    in-memory before FBX export. ``use_connect=False`` and assigning ``parent``
    in edit-mode preserve each bone's world-space head/tail.
    """
    desired_parent = "Jnt.Spine.Chest"
    children = ("Jnt.Shoulder.L", "Jnt.Shoulder.R", "Jnt.Neck")

    if desired_parent not in armature.data.bones:
        print(f"  WARNING: parent bone {desired_parent} not found; skipping spine fix")
        return

    select_only(armature)
    bpy.ops.object.mode_set(mode='EDIT')
    try:
        ebones = armature.data.edit_bones
        new_parent = ebones.get(desired_parent)
        for name in children:
            child = ebones.get(name)
            if child is None:
                print(f"  WARNING: child bone {name} not found; skipping")
                continue
            old = child.parent.name if child.parent else "<none>"
            if old == desired_parent:
                continue
            child.use_connect = False
            child.parent = new_parent
            print(f"  Re-parented {name}: {old} -> {desired_parent}")
    finally:
        bpy.ops.object.mode_set(mode='OBJECT')


def exclude_arm_helper_bones(armature):
    """Mark forearm tweak/roll helper bones as non-deform so the FBX exporter
    (with ``use_armature_deform_only=True``) drops them.

    VRChat-only: Unity orders sibling bones alphabetically when building the
    imported skeleton. The Rec Room rig parents ``Jnt.ForearmRoll.Tweak.L`` and
    ``Jnt.LowerArm.Tweak.L`` alongside ``Jnt.Hand.L`` under ``Jnt.LowerArm.L``;
    ``F`` < ``H`` < ``L`` alphabetically, so ``Hand`` ends up as the *second*
    child and the VRChat SDK warns "Hand is not first child of LowerArm: you
    may have problems with Forearm rotations". Removing those helpers from the
    exported skeleton fixes the warning. Forearm twist is handled by Unity's
    humanoid ``lowerArmTwist`` setting at runtime.

    Outside VRChat the warning is harmless and we keep these bones so any
    deformation contribution from them is preserved.
    """
    targets = (
        "Jnt.LowerArm.Tweak.L", "Jnt.LowerArm.Tweak.R",
        "Jnt.ForearmRoll.Tweak.L", "Jnt.ForearmRoll.Tweak.R",
    )
    for name in targets:
        bone = armature.data.bones.get(name)
        if bone is None:
            continue
        if bone.use_deform:
            bone.use_deform = False
            print(f"  Marked {name} non-deform (excluded from FBX export)")


def rigid_bind(target, armature):
    """Bind every vertex of ``target`` to the single deform bone whose head is
    closest to the mesh's world-space bounding-box center. Keeps the mesh rigid
    under armature deformation (no skinning distortion).
    Returns the chosen bone name, or ``None`` if no deform bones were found.
    """
    # World-space mesh center from local bounding box.
    bbox_center_local = sum((Vector(c) for c in target.bound_box), Vector()) / 8.0
    center_world = target.matrix_world @ bbox_center_local

    arm_mw = armature.matrix_world
    best_name = None
    best_dist = None
    for bone in armature.data.bones:
        if not bone.use_deform:
            continue
        head_world = arm_mw @ bone.head_local
        d = (head_world - center_world).length
        if best_dist is None or d < best_dist:
            best_dist = d
            best_name = bone.name

    for vg in list(target.vertex_groups):
        target.vertex_groups.remove(vg)
    if best_name is None:
        return None

    vg = target.vertex_groups.new(name=best_name)
    vg.add(list(range(len(target.data.vertices))), 1.0, 'REPLACE')
    return best_name


def fix_material_tints():
    """
    For each material with a Mix (Multiply) node feeding Base Color:
    1. Read the tint color from the Mix node's second color input (B / input[7]).
    2. Set ``material.diffuse_color`` and the Principled BSDF Base Color default
       to that tint (the FBX exporter writes both).
    3. Rewire: connect the texture directly to Base Color, remove the Mix node.

    Mirrors ``glb_to_fbx.py`` so the FBX exporter carries both texture and tint.
    """
    fixed = 0
    for mat in bpy.data.materials:
        if not mat.node_tree:
            continue

        principled = next((n for n in mat.node_tree.nodes if n.type == "BSDF_PRINCIPLED"), None)
        if principled is None:
            continue

        base_color_input = principled.inputs.get("Base Color")
        if base_color_input is None or not base_color_input.is_linked:
            continue

        mix_node = base_color_input.links[0].from_node
        if mix_node.bl_idname != "ShaderNodeMix":
            continue
        if not hasattr(mix_node, "blend_type") or mix_node.blend_type != "MULTIPLY":
            continue

        tint_input = mix_node.inputs[7]
        if tint_input.type != "RGBA":
            continue
        tint = tuple(tint_input.default_value)

        tex_node = None
        tex_socket = None
        tex_input = mix_node.inputs[6]
        if tex_input.is_linked:
            tex_node = tex_input.links[0].from_node
            tex_socket = tex_input.links[0].from_socket

        mat.diffuse_color = (tint[0], tint[1], tint[2], tint[3])
        base_color_input.default_value = tint

        links = mat.node_tree.links
        for link in list(links):
            if link.to_socket == base_color_input:
                links.remove(link)
        if tex_node and tex_socket:
            links.new(tex_socket, base_color_input)
        mat.node_tree.nodes.remove(mix_node)
        fixed += 1

    print(f"Fixed tint colors on {fixed} materials")
    return fixed


def unpack_textures(output_fbx):
    """Unpack every packed image to a ``<fbx>_Textures`` folder so the FBX
    exporter (with ``path_mode='AUTO'``) writes relative file references that
    Unity can re-import as standalone texture assets.
    """
    tex_dir = os.path.splitext(output_fbx)[0] + "_Textures"
    os.makedirs(tex_dir, exist_ok=True)
    saved = 0
    for img in bpy.data.images:
        if img.packed_file is None:
            continue
        ext = ".png"
        if img.file_format in ("JPEG", "JPG"):
            ext = ".jpg"
        elif img.file_format == "TARGA":
            ext = ".tga"
        tex_path = os.path.join(tex_dir, img.name + ext)
        img.unpack(method="REMOVE")
        img.filepath_raw = tex_path
        img.save()
        saved += 1
    print(f"Saved {saved} textures to {tex_dir}")
    return tex_dir


# ---------------------------------------------------------------------------
# Pipeline steps
# ---------------------------------------------------------------------------

def import_glb(glb_path):
    if not os.path.isfile(glb_path):
        raise RuntimeError(f"GLB file not found: {glb_path}")

    pre = set(bpy.data.objects.keys())
    print(f"Importing GLB: {glb_path}")
    bpy.ops.import_scene.gltf(filepath=glb_path)
    new_objs = [bpy.data.objects[n] for n in bpy.data.objects.keys() if n not in pre]
    print(f"  imported {len(new_objs)} new objects")

    avatar_root = bpy.data.objects.get("AvatarRoot")
    if avatar_root is None:
        raise RuntimeError("AvatarRoot empty was not present after GLB import.")

    # Bake AvatarRoot's world transform into each child, then reset AvatarRoot
    # to identity so the children sit directly on the Avatar_Skeleton rest pose.
    children = list(avatar_root.children)
    baked_world = [c.matrix_world.copy() for c in children]

    avatar_root.matrix_world = Matrix.Identity(4)
    bpy.context.view_layer.update()

    for child, world in zip(children, baked_world):
        child.matrix_parent_inverse.identity()
        child.matrix_local = world

    bpy.context.view_layer.update()
    return avatar_root


def rig_meshes(avatar_root, armature, rigid_names):
    targets = [c for c in avatar_root.children if c.type == 'MESH']
    print(f"Rigging {len(targets)} meshes under {avatar_root.name}")
    rigid_set = set(rigid_names or ())

    for tgt in targets:
        if base_name(tgt.name) in rigid_set:
            bone = rigid_bind(tgt, armature)
            if bone is None:
                print(f"  WARNING: no deform bone found for rigid mesh {tgt.name}")
            else:
                print(f"  {tgt.name}: rigid-bound to {bone}")
        else:
            src = pick_weight_source(tgt.name)
            if src is None:
                print(f"  WARNING: no weight donor found for {tgt.name}; leaving unrigged")
            else:
                transfer_weights(tgt, src)
                print(f"  {tgt.name}: weights from {src.name} ({len(tgt.vertex_groups)} groups)")

        for m in list(tgt.modifiers):
            if m.type == 'ARMATURE':
                tgt.modifiers.remove(m)
        mod = tgt.modifiers.new(name="Armature", type='ARMATURE')
        mod.object = armature
        mod.use_vertex_groups = True

    return targets


def export_fbx(output_fbx, avatar_root, targets, armature):
    select_only(armature, avatar_root, *targets)

    out_dir = os.path.dirname(output_fbx)
    if out_dir:
        os.makedirs(out_dir, exist_ok=True)

    print(f"Exporting FBX: {output_fbx}")
    bpy.ops.export_scene.fbx(
        filepath=output_fbx,
        use_selection=True,
        object_types={'ARMATURE', 'MESH', 'EMPTY'},
        apply_scale_options='FBX_SCALE_ALL',
        axis_forward='Z',
        axis_up='Y',
        use_space_transform=True,
        bake_space_transform=False,
        mesh_smooth_type='FACE',
        use_mesh_modifiers=True,
        add_leaf_bones=False,
        use_armature_deform_only=True,
        primary_bone_axis='Y',
        secondary_bone_axis='X',
        path_mode='AUTO',
        embed_textures=False,
        colors_type='SRGB',
        bake_anim=False,
    )


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

def main():
    glb_path, output_fbx, rigid_names, delete_names, vrchat = parse_args()
    print(f"GLB:    {glb_path}")
    print(f"Output: {output_fbx}")
    if rigid_names:
        print(f"Rigid:  {rigid_names}")
    if delete_names:
        print(f"Delete: {delete_names}")
    if vrchat:
        print("VRChat: enabled")

    armature = bpy.data.objects.get("Avatar_Skeleton")
    if armature is None or armature.type != 'ARMATURE':
        raise RuntimeError("Avatar_Skeleton armature not found in rigged_reference.blend")

    avatar_root = import_glb(glb_path)

    # Rename any mesh whose materials identify it as the avatar's base skin
    # (the GLB names skin meshes by index, so we detect them by material).
    rename_skin_meshes(avatar_root)

    # Remove any meshes the caller asked to delete (e.g. an off-hand watch)
    # before rigging so they don't get weight-transferred or exported.
    if delete_names:
        delete_set = set(delete_names)
        for child in list(avatar_root.children):
            if base_name(child.name) in delete_set and child.type == 'MESH':
                print(f"Deleting mesh: {child.name}")
                mesh_data = child.data
                bpy.data.objects.remove(child, do_unlink=True)
                if mesh_data is not None and mesh_data.users == 0:
                    bpy.data.meshes.remove(mesh_data)

    targets = rig_meshes(avatar_root, armature, rigid_names)
    fix_spine_hierarchy(armature)
    if vrchat:
        exclude_arm_helper_bones(armature)
    fix_material_tints()
    unpack_textures(output_fbx)
    export_fbx(output_fbx, avatar_root, targets, armature)
    print("Done!")


if __name__ == "__main__":
    main()

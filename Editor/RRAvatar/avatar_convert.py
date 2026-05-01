"""
Blender headless script: Convert a Rec Room avatar GLB into a rigged FBX.

Workflow (the .blend opened by Blender must be the bundled fb_library.blend,
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
    blender fb_library.blend --background --python avatar_convert.py -- input.glb output.fbx
"""

import bpy
import os
import sys

from mathutils import Matrix, Quaternion, Vector


# Static corrective offset that aligns the GLB's AvatarRoot meshes with
# fb_library.blend's Avatar_Skeleton rest pose. Determined empirically by
# inspecting a known-good aligned scene; the GLB always imports AvatarRoot at
# this same broken transform, so we bake the inverse into the children.
AVATAR_ROOT_OFFSET_LOCATION = Vector((-0.031233, 0.04646, 0.010582))
# Quaternion is (w, x, y, z) to match Blender's mathutils.Quaternion ctor.
AVATAR_ROOT_OFFSET_ROTATION = Quaternion((0.018, 0.018, 0.004, 1.000))


# ---------------------------------------------------------------------------
# Argument parsing
# ---------------------------------------------------------------------------

def parse_args():
    if "--" in sys.argv:
        args = sys.argv[sys.argv.index("--") + 1:]
        if len(args) >= 2:
            return args[0], args[1]
    raise RuntimeError(
        "Usage: blender fb_library.blend --background --python avatar_convert.py "
        "-- input.glb output.fbx"
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

    # Apply the corrective offset to AvatarRoot, then bake the resulting
    # world transform into each child and reset AvatarRoot to identity.
    # We capture matrix_world (which already accounts for matrix_parent_inverse
    # set by the glTF importer) rather than composing offset @ matrix_local
    # directly, so the bake is correct regardless of the importer's
    # parent-inverse state.
    offset = (
        Matrix.Translation(AVATAR_ROOT_OFFSET_LOCATION)
        @ AVATAR_ROOT_OFFSET_ROTATION.normalized().to_matrix().to_4x4()
    )
    avatar_root.matrix_world = offset
    bpy.context.view_layer.update()

    children = list(avatar_root.children)
    baked_world = [c.matrix_world.copy() for c in children]

    avatar_root.matrix_world = Matrix.Identity(4)
    bpy.context.view_layer.update()

    for child, world in zip(children, baked_world):
        child.matrix_parent_inverse.identity()
        child.matrix_local = world

    bpy.context.view_layer.update()
    return avatar_root


def rig_meshes(avatar_root, armature):
    targets = [c for c in avatar_root.children if c.type == 'MESH']
    print(f"Rigging {len(targets)} meshes under {avatar_root.name}")

    for tgt in targets:
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
        axis_forward='-Z',
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
    glb_path, output_fbx = parse_args()
    print(f"GLB:    {glb_path}")
    print(f"Output: {output_fbx}")

    armature = bpy.data.objects.get("Avatar_Skeleton")
    if armature is None or armature.type != 'ARMATURE':
        raise RuntimeError("Avatar_Skeleton armature not found in fb_library.blend")

    avatar_root = import_glb(glb_path)
    targets = rig_meshes(avatar_root, armature)
    fix_material_tints()
    unpack_textures(output_fbx)
    export_fbx(output_fbx, avatar_root, targets, armature)
    print("Done!")


if __name__ == "__main__":
    main()

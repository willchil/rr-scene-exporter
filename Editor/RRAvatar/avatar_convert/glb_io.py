"""GLB import + FBX export + texture unpacking. All Blender-level file I/O lives here."""

import os

import bpy
from mathutils import Matrix

from .utils import select_only


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

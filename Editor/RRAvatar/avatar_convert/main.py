"""Argument parsing + pipeline entry point.

Blender invokes the sibling ``avatar_convert.py`` launcher via
``--python``; that shim adds this package's parent directory to
``sys.path`` and calls :func:`main`. Anything after Blender's ``--``
sentinel is parsed by :func:`parse_args` (positional GLB/FBX paths,
mesh-name buckets, and the standalone toggle flags).
"""

import sys

import bpy

from .utils import base_name
from .bones import exclude_arm_helper_bones, fix_spine_hierarchy
from .glb_io import export_fbx, import_glb, unpack_textures
from .materials import fix_material_tints
from .meshes import merge_skinned_meshes, rename_meshes_by_material
from .rigging import rig_meshes
from .tpose import force_tpose


def parse_args():
    if "--" in sys.argv:
        args = sys.argv[sys.argv.index("--") + 1:]
        if len(args) >= 2:
            # Trailing args are mesh names: bare names mark rigid binds; names
            # after a ``--delete`` marker are removed from the avatar before
            # rigging. ``--vrchat``, ``--merge-meshes`` and ``--enforce-tpose``
            # are standalone toggles (no value) for the corresponding
            # optional rig-shaping passes.
            rigid = []
            delete = []
            vrchat = False
            merge = False
            tpose = False
            bucket = rigid
            for a in args[2:]:
                if a == "--delete":
                    bucket = delete
                    continue
                if a == "--vrchat":
                    vrchat = True
                    continue
                if a == "--merge-meshes":
                    merge = True
                    continue
                if a == "--enforce-tpose":
                    tpose = True
                    continue
                if a:
                    bucket.append(a)
            return args[0], args[1], rigid, delete, vrchat, merge, tpose
    raise RuntimeError(
        "Usage: blender rigged_reference.blend --background --python avatar_convert.py "
        "-- input.glb output.fbx [rigid_mesh_name ...] [--delete mesh_name ...] "
        "[--vrchat] [--merge-meshes] [--enforce-tpose]"
    )


def main():
    glb_path, output_fbx, rigid_names, delete_names, vrchat, merge, tpose = parse_args()
    print(f"GLB:    {glb_path}")
    print(f"Output: {output_fbx}")
    if rigid_names:
        print(f"Rigid:  {rigid_names}")
    if delete_names:
        print(f"Delete: {delete_names}")
    if vrchat:
        print("VRChat: enabled")
    if merge:
        print("Merge:  enabled")
    if tpose:
        print("T-Pose: enabled")

    armature = bpy.data.objects.get("Avatar_Skeleton")
    if armature is None or armature.type != 'ARMATURE':
        raise RuntimeError("Avatar_Skeleton armature not found in rigged_reference.blend")

    avatar_root = import_glb(glb_path)

    # Remove any meshes the caller asked to delete (e.g. an off-hand watch)
    # before rigging so they don't get weight-transferred or exported. Runs
    # BEFORE rename_meshes_by_material so the watch's raw GLB node name
    # (which is what Unity sends, since the cleaned material name doesn't
    # disambiguate L from R) still matches the Blender object name.
    if delete_names:
        delete_set = set(delete_names)
        for child in list(avatar_root.children):
            if base_name(child.name) in delete_set and child.type == 'MESH':
                print(f"Deleting mesh: {child.name}")
                mesh_data = child.data
                bpy.data.objects.remove(child, do_unlink=True)
                if mesh_data is not None and mesh_data.users == 0:
                    bpy.data.meshes.remove(mesh_data)

    # Rename every surviving mesh to its first material's cleaned name (and
    # any watch mesh to the literal "Watch") so the rigid name match below
    # and the eventual FBX hierarchy carry the same friendly names the
    # Unity-side UI showed to the user.
    rename_meshes_by_material(avatar_root)

    targets = rig_meshes(avatar_root, armature, rigid_names)
    fix_spine_hierarchy(armature)
    if vrchat:
        exclude_arm_helper_bones(armature, targets)
    if tpose:
        force_tpose(armature, targets)
    if merge:
        targets = merge_skinned_meshes(avatar_root, targets)
    fix_material_tints()
    unpack_textures(output_fbx)
    export_fbx(output_fbx, avatar_root, targets, armature)
    print("Done!")

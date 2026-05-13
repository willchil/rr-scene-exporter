"""Mesh-level fixups: name meshes after their material + merging for export."""

import bpy

from .utils import base_name, select_only


# Rec Room exports its avatar materials with their Unity runtime suffixes
# baked into the name. Strip those, plus the conventional ``mat_`` /
# ``_mat`` decorations, to derive a clean mesh name.
_UNITY_NAME_SUFFIXES = ("(Instance)", "(Clone)")


def clean_material_name(name):
    if not name:
        return name
    # rigged_reference.blend may already define a material with this name, in
    # which case Blender appends ``.001`` (etc.) when importing the GLB --
    # strip that before we look for the Unity-runtime suffixes.
    name = base_name(name).rstrip()
    # Names can carry ``(Clone)``, ``(Instance)`` or both (in either order,
    # depending on whether the source was a prefab clone, an instanced
    # material, or both). Peel them off one at a time.
    changed = True
    while changed:
        changed = False
        for suffix in _UNITY_NAME_SUFFIXES:
            if name.endswith(suffix):
                name = name[:-len(suffix)].rstrip()
                changed = True
    if name.lower().startswith("mat_"):
        name = name[4:]
    if name.lower().endswith("_mat"):
        name = name[:-4]
    return name


def rename_meshes_by_material(avatar_root):
    """Rename every mesh under ``avatar_root`` to its first material's cleaned
    name. Meshes whose raw GLB node name identifies them as a watch are
    instead renamed to the literal ``Watch`` so the Unity-side UI's single
    ``Watch`` toggle (and any post-conversion references) line up regardless
    of which side survived the off-hand deletion. Meshes without a usable
    material keep their original name. Blender auto-suffixes collisions with
    ``.001``, ``.002``, ... so duplicates remain unique on the data side;
    ``base_name`` strips that suffix when matching against caller-supplied
    rigid/delete names.
    """
    for child in list(avatar_root.children):
        if child.type != 'MESH':
            continue
        new_name = None
        if "watch" in child.name.lower():
            new_name = "Watch"
        elif child.data is not None:
            for mat in child.data.materials:
                if mat is None:
                    continue
                cleaned = clean_material_name(mat.name)
                if cleaned:
                    new_name = cleaned
                    break
        if new_name and new_name != child.name:
            old = child.name
            child.name = new_name
            print(f"Renamed mesh: {old} -> {child.name}")


def merge_skinned_meshes(avatar_root, targets, name="Body"):
    """Join every mesh in ``targets`` into a single mesh so the FBX produces
    one ``SkinnedMeshRenderer`` in Unity.

    Helps the VRChat performance ranking (which caps "Skinned Mesh Renderers"
    at 1 for the highest tier) and is generally a draw-call win elsewhere.
    Blender's ``object.join`` unions vertex groups by name, shape keys by
    name, and material slots by reference, so the merged mesh keeps every
    weight, blendshape and material from its sources -- it just lives under
    one renderer with multiple submeshes.

    Assumes every mesh in ``targets`` is already a real skinned mesh (rigid
    binds have been converted by ``rigid_bind`` to a single 100%-weighted
    vertex group on the target bone, and ``rig_meshes`` has added an Armature
    modifier to all of them).

    Returns the new ``targets`` list (a single-element list containing the
    merged mesh, or the original list if there is nothing to merge).
    """
    meshes = [t for t in targets if t and t.type == 'MESH' and t.name in bpy.data.objects]
    if len(meshes) <= 1:
        return meshes

    primary = meshes[0]
    select_only(*meshes)
    bpy.context.view_layer.objects.active = primary
    bpy.ops.object.join()

    # Rename the survivor and its mesh datablock so Unity gets a clean
    # "Body" SkinnedMeshRenderer instead of whatever the first source mesh
    # happened to be called.
    primary.name = name
    if primary.data is not None:
        primary.data.name = name

    print(f"Merged {len(meshes)} meshes into {primary.name} "
          f"({len(primary.data.materials)} material slots, "
          f"{len(primary.vertex_groups)} vertex groups)")
    return [primary]

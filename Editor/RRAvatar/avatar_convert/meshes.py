"""Mesh-level fixups: skin-mesh rename + merging skinned meshes for export."""

import bpy

from .utils import select_only


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

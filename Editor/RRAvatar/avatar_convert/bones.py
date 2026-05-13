"""Bone-hierarchy fixups (spine re-parenting + forearm helper exclusion)."""

import bpy

from .utils import select_only


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


def _fold_vertex_group(mesh_obj, src_name, dst_name):
    """Move all vertex weights from group ``src_name`` onto group ``dst_name``
    (creating the destination if needed) on ``mesh_obj``, then remove the
    source group. Existing weights on the destination are *added* to. Returns
    the number of weighted vertices moved.
    """
    if mesh_obj is None or mesh_obj.type != 'MESH' or mesh_obj.data is None:
        return 0
    src = mesh_obj.vertex_groups.get(src_name)
    if src is None:
        return 0
    dst = mesh_obj.vertex_groups.get(dst_name)
    if dst is None:
        dst = mesh_obj.vertex_groups.new(name=dst_name)
    src_index = src.index
    moved = 0
    for v in mesh_obj.data.vertices:
        for g in v.groups:
            if g.group == src_index and g.weight > 0.0:
                dst.add([v.index], g.weight, 'ADD')
                moved += 1
                break
    mesh_obj.vertex_groups.remove(src)
    return moved


def exclude_arm_helper_bones(armature, meshes):
    """Mark forearm tweak/roll helper bones as non-deform so the FBX exporter
    (with ``use_armature_deform_only=True``) drops them, after first folding
    any vertex weights they own onto their parent deform bone.

    VRChat-only: Unity orders sibling bones alphabetically when building the
    imported skeleton. The Rec Room rig parents ``Jnt.ForearmRoll.Tweak.L`` and
    ``Jnt.LowerArm.Tweak.L`` alongside ``Jnt.Hand.L`` under ``Jnt.LowerArm.L``;
    ``F`` < ``H`` < ``L`` alphabetically, so ``Hand`` ends up as the *second*
    child and the VRChat SDK warns "Hand is not first child of LowerArm: you
    may have problems with Forearm rotations". Removing those helpers from the
    exported skeleton fixes the warning. Forearm twist is handled by Unity's
    humanoid ``lowerArmTwist`` setting at runtime.

    The Rec Room body actually skins to these helpers (they're not pure twist
    bones), so we must redistribute their weights onto the parent deform bone
    first or the affected forearm vertices end up referencing missing bones
    and spike to world origin in Unity.

    Outside VRChat the warning is harmless and we keep these bones so any
    deformation contribution from them is preserved.
    """
    # Helper-bone name -> fallback parent (used if the bone is missing from the
    # armature entirely). When the bone exists we read the real parent off it.
    helpers = {
        "Jnt.LowerArm.Tweak.L":   "Jnt.LowerArm.L",
        "Jnt.LowerArm.Tweak.R":   "Jnt.LowerArm.R",
        "Jnt.ForearmRoll.Tweak.L": "Jnt.LowerArm.L",
        "Jnt.ForearmRoll.Tweak.R": "Jnt.LowerArm.R",
    }
    for name, fallback_parent in helpers.items():
        bone = armature.data.bones.get(name)
        parent_name = fallback_parent
        if bone is not None and bone.parent is not None:
            # Walk up to the first ancestor that will still deform after we're
            # done; in practice the immediate parent is already correct.
            parent_name = bone.parent.name

        total_moved = 0
        for m in meshes:
            total_moved += _fold_vertex_group(m, name, parent_name)
        if total_moved:
            print(f"  Folded {total_moved} weights from {name} -> {parent_name}")

        if bone is not None and bone.use_deform:
            bone.use_deform = False
            print(f"  Marked {name} non-deform (excluded from FBX export)")

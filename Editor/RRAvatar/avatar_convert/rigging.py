"""Per-mesh rigging: weight-transfer donors, rigid binds, and the rig-all-meshes pipeline step."""

import bpy
from mathutils import Vector

from .utils import base_name, select_only


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

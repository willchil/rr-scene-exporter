"""Per-mesh rigging: weight-transfer donors, rigid binds, and the rig-all-meshes pipeline step."""

import bpy
from mathutils import Vector

from .utils import base_name, select_only


# Rigged_reference.blend ships two body donors that share Avatar_Skeleton:
# the Full-Body Avatar (FBA) mesh and the legless Mobile Bean (MB) torso.
# The Bean has its own (denser) weight painting around the truncated torso
# and is the right donor whenever the GLB lacks any watch mesh -- watches
# are FBA-only attachments, so their absence is the canonical Bean signal.
_FBA_BODY_DONOR = "BodyMesh_LOD0"
_BEAN_BODY_DONOR = "MB_BodyMesh_LOD0"


def pick_weight_source(target_name, bean=False):
    """Return the FB / MB mesh whose weights should be transferred onto
    ``target_name``. ``bean=True`` selects the Mobile Bean torso donor for
    body-class meshes; watches always pull from their dedicated donors.
    """
    if "Wrist_Watch_L" in target_name:
        return bpy.data.objects.get("Wrist_Watch_L_LOD0")
    if "Wrist_Watch_R" in target_name:
        return bpy.data.objects.get("Wrist_Watch_R_LOD0")
    # Everything else (body, head, hands, accessories) takes weights from the body.
    return bpy.data.objects.get(_BEAN_BODY_DONOR if bean else _FBA_BODY_DONOR)


def transfer_weights(target, source, armature=None):
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

    # Drop any vertex groups whose name doesn't match a bone before
    # normalising. The MB (Bean) donor carries ~58 ``Msk.*`` clothing /
    # region mask groups in addition to the deform-bone groups; if those
    # survive into the avatar mesh they (a) eat weight share when
    # ``vertex_group_normalize_all`` divides each vertex's totals, leaving
    # the bone weights summing to < 1.0, and (b) get dropped silently by
    # the FBX exporter, so Unity ends up with under-weighted vertices that
    # lag behind the bones (visible as hand stretching during animation
    # and as a partial A-pose after :func:`force_tpose`'s weight-based
    # rest-pose rotation only moves vertices part of the way).
    if armature is not None:
        bone_names = {b.name for b in armature.data.bones}
        removed = 0
        for vg in list(target.vertex_groups):
            if vg.name not in bone_names:
                target.vertex_groups.remove(vg)
                removed += 1
        if removed:
            print(f"    stripped {removed} non-bone vertex groups from {target.name}")

    # Re-normalise so each vertex's weights sum to 1.0 (cleanup after the
    # interpolated transfer, which can leave fractional totals).
    select_only(target)
    if target.vertex_groups:
        bpy.ops.object.vertex_group_normalize_all(lock_active=False)


def clean_bean_finger_weights(target):
    """Bean (legless) avatars have a four-digit hand: a thumb, an index
    finger, and a single "mitten" digit that stands in for the middle,
    ring, and pinky fingers. The MB donor only paints the mitten with
    ``Middle{n}`` weight (no Ring or Pinky), so the mitten responds to
    a single Unity humanoid finger muscle (Middle) instead of moving as
    the average of all three.

    This pass leaves the index/mitten classification of weights alone
    and only redistributes mitten weight: for every vertex with any
    ``Middle{n}`` weight on either side, split that weight equally
    across ``Middle{n}``, ``Ring{n}``, ``Pinky{n}`` (creating the
    Ring/Pinky vertex groups on the target if the donor lacked them).
    Linear blend skinning with equal weights on those bones evaluates
    to the average of their three transforms, so the mitten moves as
    the mean of the three Unity humanoid fingers driving it.
    """
    name_to_idx = {vg.name: vg.index for vg in target.vertex_groups}

    def _ensure_group(bone_name):
        if bone_name not in name_to_idx:
            vg = target.vertex_groups.new(name=bone_name)
            name_to_idx[bone_name] = vg.index
        return target.vertex_groups[name_to_idx[bone_name]]

    mesh = target.data

    for side in ('L', 'R'):
        mitten_per_joint = [
            [f"Jnt.Hand.Middle{n}.{side}",
             f"Jnt.Hand.Ring{n}.{side}",
             f"Jnt.Hand.Pinky{n}.{side}"]
            for n in (1, 2, 3)
        ]

        # The donor only supplies Middle weight, so use that to detect
        # whether this side has any mitten weighting at all.
        middle_present = any(
            f"Jnt.Hand.Middle{n}.{side}" in name_to_idx for n in (1, 2, 3)
        )
        if not middle_present:
            continue

        # Make sure every Ring/Pinky bone has a vertex group on the
        # target so the equal three-way split has somewhere to write to.
        for joint in mitten_per_joint:
            for bone in joint:
                _ensure_group(bone)
        mitten_idx_per_joint = [
            [name_to_idx[b] for b in joint] for joint in mitten_per_joint
        ]

        # Per joint segment, the source group (Middle{n}) is also the
        # first entry of the destination joint -- so reading current
        # weights from the source and overwriting all three entries with
        # the equal share is safe.
        n_verts = 0
        for v in mesh.vertices:
            touched = False
            for jn in (1, 2, 3):
                source_idx = name_to_idx.get(f"Jnt.Hand.Middle{jn}.{side}")
                if source_idx is None:
                    continue
                middle_w = 0.0
                for g in v.groups:
                    if g.group == source_idx:
                        middle_w = g.weight
                        break
                if middle_w <= 0.0:
                    continue
                share = middle_w / 3.0
                for gi in mitten_idx_per_joint[jn - 1]:
                    target.vertex_groups[gi].add([v.index], share, 'REPLACE')
                touched = True
            if touched:
                n_verts += 1

        if n_verts:
            print(f"    bean hand {side}: averaged mitten weight across "
                  f"Middle/Ring/Pinky on {n_verts} verts of {target.name}")

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


def rig_meshes(avatar_root, armature, rigid_names, bean=False):
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
            src = pick_weight_source(tgt.name, bean=bean)
            if src is None:
                print(f"  WARNING: no weight donor found for {tgt.name}; leaving unrigged")
            else:
                transfer_weights(tgt, src, armature=armature)
                if bean:
                    clean_bean_finger_weights(tgt)
                print(f"  {tgt.name}: weights from {src.name} ({len(tgt.vertex_groups)} groups)")

        for m in list(tgt.modifiers):
            if m.type == 'ARMATURE':
                tgt.modifiers.remove(m)
        mod = tgt.modifiers.new(name="Armature", type='ARMATURE')
        mod.object = armature
        mod.use_vertex_groups = True

    return targets

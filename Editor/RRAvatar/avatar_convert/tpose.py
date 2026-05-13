"""Convert the source rig's A-pose rest into a humanoid T-pose.

Done as a pure rest-pose change (edit-bone + weighted vertex transform)
because ``bpy.ops.object.modifier_apply`` refuses to run on meshes that have
shape keys, which the Rec Room face mesh always does.
"""

import bpy
from mathutils import Matrix, Vector

from .utils import select_only


def _force_tpose_arm(armature, meshes, root_bone_name, target_dir_world):
    """Pivot every bone in the subtree under ``root_bone_name`` (and the
    matching portion of each mesh's geometry, including shape keys) so the
    bone points along ``target_dir_world`` in *world* space.

    The rotation is applied as a pure rest-pose change, with the mesh and its
    shape keys carried along by weighted skinning -- equivalent to Blender's
    "Apply Pose As Rest" workflow but without going through
    ``modifier_apply`` (which refuses to run on meshes with shape keys).
    """
    root = armature.data.bones.get(root_bone_name)
    if root is None:
        print(f"  WARNING: T-pose source bone {root_bone_name} not found")
        return

    arm_mw = armature.matrix_world
    arm_rot_inv = arm_mw.to_3x3().inverted()
    target_dir_arm = (arm_rot_inv @ target_dir_world).normalized()

    pivot = root.head_local.copy()
    cur_dir = (root.tail_local - pivot).normalized()
    rot_quat = cur_dir.rotation_difference(target_dir_arm)
    if rot_quat.angle < 1e-4:
        return

    rot_4x4 = rot_quat.to_matrix().to_4x4()
    arm_transform = Matrix.Translation(pivot) @ rot_4x4 @ Matrix.Translation(-pivot)

    # Collect every bone in the subtree, ordered root-first (parent before
    # child) so that if any bones are use_connect=True the parent's tail-move
    # implicitly carries the child's head before we explicitly transform it.
    subtree_order = []
    seen = set()
    stack = [root]
    while stack:
        b = stack.pop()
        if b.name in seen:
            continue
        seen.add(b.name)
        subtree_order.append(b)
        # Reverse so children pop in declaration order; doesn't matter for
        # correctness, just keeps the print order stable.
        stack.extend(reversed(list(b.children)))
    subtree_names = {b.name for b in subtree_order}

    print(f"  T-pose: rotated {root_bone_name} subtree by "
          f"{rot_quat.angle * 180.0 / 3.141592653589793:.1f} deg")

    # Rotate the rest pose. For each subtree bone, move its head/tail by the
    # pivot transform and re-align its roll so the local Z axis rotates with
    # it (otherwise the bone twists in place). use_mirror_x must be off or
    # Blender will auto-mirror every L edit onto the corresponding R bone
    # (and vice versa), leaving the opposite arm's bones in T-pose without
    # the matching mesh transform.
    select_only(armature)
    bpy.ops.object.mode_set(mode='EDIT')
    prev_mirror = armature.data.use_mirror_x
    armature.data.use_mirror_x = False
    try:
        ebones = armature.data.edit_bones
        for b in subtree_order:
            eb = ebones.get(b.name)
            if eb is None:
                continue
            new_z = rot_quat @ eb.matrix.col[2].xyz
            eb.head = arm_transform @ eb.head
            eb.tail = arm_transform @ eb.tail
            eb.align_roll(new_z)
    finally:
        armature.data.use_mirror_x = prev_mirror
        bpy.ops.object.mode_set(mode='OBJECT')

    # Transform each mesh's geometry. Vertex weights to the rotated subtree
    # determine how much of the rotation each vertex receives -- a vertex
    # 100% weighted to UpperArm rotates fully; a shoulder vertex split with
    # the spine rotates only partially, which is what the Armature modifier
    # would have produced.
    for m in meshes:
        if m is None or m.type != 'MESH' or m.data is None:
            continue
        subtree_indices = {vg.index for vg in m.vertex_groups if vg.name in subtree_names}
        if not subtree_indices:
            continue

        # Convert the armature-space transform into this mesh's object space.
        mesh_mw = m.matrix_world
        arm_to_mesh = mesh_mw.inverted() @ arm_mw
        mesh_transform = arm_to_mesh @ arm_transform @ arm_to_mesh.inverted()

        verts = m.data.vertices
        n = len(verts)
        weights = [0.0] * n
        for i, v in enumerate(verts):
            w = 0.0
            for g in v.groups:
                if g.group in subtree_indices:
                    w += g.weight
            weights[i] = min(w, 1.0)

        def _xform(co, w):
            if w <= 0.0:
                return co
            rotated = mesh_transform @ co
            if w >= 1.0:
                return rotated
            return co.lerp(rotated, w)

        for i, v in enumerate(verts):
            w = weights[i]
            if w > 0.0:
                v.co = _xform(v.co, w)

        if m.data.shape_keys:
            for kb in m.data.shape_keys.key_blocks:
                for i, p in enumerate(kb.data):
                    w = weights[i]
                    if w > 0.0:
                        p.co = _xform(p.co, w)


def _straighten_subtree(armature, meshes, root_bone_name, target_dir_world):
    """Recursively re-align every bone in the subtree of ``root_bone_name``
    so that each bone individually points along ``target_dir_world``.

    The shoulder-level pass in ``_force_tpose_arm`` rotates the whole arm
    chain rigidly, which preserves the source A-pose's relative finger curl.
    Walking the subtree parent-first and re-aligning each bone in turn
    flattens that curl: every joint ends up pointing along the arm axis,
    matching what Unity's "Enforce T-Pose" produces.

    Thumb bones are deliberately excluded: Unity's T-pose convention has the
    thumb sticking out from the palm at roughly 30 degrees, and the source
    A-pose's thumb-vs-hand orientation already matches that closely once the
    arm is rotated rigidly. Straightening thumbs to the arm axis tucks them
    under the index finger, which reads as a balled fist in VRChat.
    """
    root = armature.data.bones.get(root_bone_name)
    if root is None:
        return
    # Collect names BFS-style (parent before child) up front, because each
    # _force_tpose_arm call enters/exits edit mode and rebuilds ``bones``.
    # Skip any bone whose name contains "Thumb" (and its subtree) -- the
    # thumb keeps its A-pose-relative angle on purpose.
    names = []
    queue = [root]
    while queue:
        b = queue.pop(0)
        if "Thumb" in b.name:
            continue
        names.append(b.name)
        queue.extend(b.children)
    for name in names:
        _force_tpose_arm(armature, meshes, name, target_dir_world)


def force_tpose(armature, meshes):
    """Convert the rig's rest pose from Rec Room A-pose to humanoid T-pose.

    Unity humanoid (and the VRChat SDK in particular) calibrates muscle
    space relative to T-pose, so an A-pose rest causes shipped animations
    (claps, dances, etc.) to drive arms tucked into the torso. Rotating each
    upper-arm subtree so the arm points along world +/-X fixes this and
    matches what the importer's "Enforce T-Pose" button would do, but baked
    into the FBX itself so every consumer (humanoid, generic, raw skeleton)
    sees a consistent rest pose.

    After the shoulder-level rotation, the hand subtree is re-straightened
    bone-by-bone so the source A-pose's relaxed finger curl (which would
    otherwise survive the rigid arm rotation) is flattened along the arm
    axis. Without this fingers ship slightly curled and read as a balled
    fist in VRChat's shipped animations.
    """
    _force_tpose_arm(armature, meshes, "Jnt.UpperArm.L", Vector(( 1.0, 0.0, 0.0)))
    _straighten_subtree(armature, meshes, "Jnt.Hand.L", Vector(( 1.0, 0.0, 0.0)))
    _force_tpose_arm(armature, meshes, "Jnt.UpperArm.R", Vector((-1.0, 0.0, 0.0)))
    _straighten_subtree(armature, meshes, "Jnt.Hand.R", Vector((-1.0, 0.0, 0.0)))

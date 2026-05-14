"""Convert the source rig's A-pose rest into a humanoid T-pose.

Each bone's target world orientation is taken from a reference Rec Room
avatar that has been run through Unity's "Enforce T-Pose" button (saved
out as ``Avatar_Skeleton.prefab``). The Unity prefab's per-bone local
rotations are composed into Unity-world rotations (with the chest
treated as identity, which holds for any properly upright humanoid in
T-pose), Unity's local +Y bone axis is rotated through to obtain the
world tail direction, and that direction is converted into Blender's
coordinate frame and fed bone-by-bone into :func:`_force_tpose_arm`,
which applies the rotation as a pure rest-pose change and carries the
mesh + shape keys along by weighted skinning.

Done as a rest-pose change (edit-bone + weighted vertex transform)
because ``bpy.ops.object.modifier_apply`` refuses to run on meshes that
have shape keys, which the Rec Room face mesh always does.
"""

import bpy
from mathutils import Matrix, Quaternion, Vector

from .utils import select_only


# Per-bone local rotations from a Unity humanoid avatar after pressing
# "Enforce T-Pose" in the Rig inspector. Stored as Blender quaternions
# (``Quaternion((w, x, y, z))``) for ergonomic composition; the source
# values are Unity ``(x, y, z, w)`` quaternions read straight from the
# prefab YAML.
def _q(x, y, z, w):
    return Quaternion((w, x, y, z))


# Each entry: bone -> (parent-bone-or-None, local-rotation-quat). Parent
# of ``None`` means "compose from world identity" (i.e. the bone's
# ancestor chain above this point is assumed upright). Shoulder is
# included so its contribution to UpperArm's world rotation is captured,
# but Shoulder itself is *not* in the update list -- the Rec Room rig's
# shoulder is already correct in A-pose, and rewriting it would shift
# the arm root sideways.
_UNITY_TPOSE = {
    # Left side -----------------------------------------------------------
    "Jnt.Shoulder.L":      (None,                  _q(-0.016470691, 0.008238867, -0.70764357, 0.7063295)),
    "Jnt.UpperArm.L":      ("Jnt.Shoulder.L",      _q(0.017935008, -0.020285366, -0.023550447, 0.9993559)),
    "Jnt.LowerArm.L":      ("Jnt.UpperArm.L",      _q(-0.037070952, 0.0009058099, 0.008189214, 0.9992787)),
    "Jnt.Hand.L":          ("Jnt.LowerArm.L",      _q(0.028521225, 0.0021384284, 0.0020113466, 0.9995889)),
    "Jnt.Hand.PalmCup.L":  ("Jnt.Hand.L",          _q(0.14998166, -0.36801282, -0.050032724, 0.91627985)),
    "Jnt.Hand.Thumb1.L":   ("Jnt.Hand.L",          _q(0.086092845, 0.9268821, -0.34460896, -0.121335834)),
    "Jnt.Hand.Thumb2.L":   ("Jnt.Hand.Thumb1.L",   _q(-0.027821762, -0.009157237, -0.019018158, 0.99939007)),
    "Jnt.Hand.Thumb3.L":   ("Jnt.Hand.Thumb2.L",   _q(0.036700882, -0.0114988675, 0.0070044138, 0.99923563)),
    "Jnt.Hand.Index1.L":   ("Jnt.Hand.L",          _q(-0.032732654, -0.7167498, -0.0046872115, 0.696546)),
    "Jnt.Hand.Index2.L":   ("Jnt.Hand.Index1.L",   _q(-0.032750335, -0.028910723, -0.00017565115, 0.9990454)),
    "Jnt.Hand.Index3.L":   ("Jnt.Hand.Index2.L",   _q(-0.083374664, -0.0064752656, 0.006052037, 0.99647886)),
    "Jnt.Hand.Middle1.L":  ("Jnt.Hand.L",          _q(-0.025620677, -0.71195036, -0.028919961, 0.7011661)),
    "Jnt.Hand.Middle2.L":  ("Jnt.Hand.Middle1.L",  _q(-0.035435125, -0.023255402, -2.1265814e-05, 0.9991014)),
    "Jnt.Hand.Middle3.L":  ("Jnt.Hand.Middle2.L",  _q(-0.1218577, 0.0050440333, 0.0032211929, 0.9925296)),
    "Jnt.Hand.Ring1.L":    ("Jnt.Hand.L",          _q(-0.008290451, -0.6606433, -0.040058766, 0.74958456)),
    "Jnt.Hand.Ring2.L":    ("Jnt.Hand.Ring1.L",    _q(-0.034833908, 0.0008356868, -0.001722736, 0.9993913)),
    "Jnt.Hand.Ring3.L":    ("Jnt.Hand.Ring2.L",    _q(-0.15707459, -0.014415814, -0.0027186016, 0.9874778)),
    "Jnt.Hand.Pinky1.L":   ("Jnt.Hand.PalmCup.L",  _q(-0.074653596, -0.23051965, 0.07255431, 0.9674831)),
    "Jnt.Hand.Pinky2.L":   ("Jnt.Hand.Pinky1.L",   _q(-0.032221537, -0.010016466, 0.0019482549, 0.99942875)),
    "Jnt.Hand.Pinky3.L":   ("Jnt.Hand.Pinky2.L",   _q(-0.14490606, -0.0071131526, -0.002072785, 0.9894177)),

    # Right side ----------------------------------------------------------
    "Jnt.Shoulder.R":      (None,                  _q(-0.016470967, -0.008239146, 0.70764357, 0.7063295)),
    "Jnt.UpperArm.R":      ("Jnt.Shoulder.R",      _q(0.017935041, 0.020285398, 0.023550447, 0.9993559)),
    "Jnt.LowerArm.R":      ("Jnt.UpperArm.R",      _q(-0.037071243, -0.0009057167, -0.008189261, 0.9992787)),
    "Jnt.Hand.R":          ("Jnt.LowerArm.R",      _q(0.028521234, -0.0021384314, -0.002011287, 0.9995889)),
    "Jnt.Hand.PalmCup.R":  ("Jnt.Hand.R",          _q(0.14998162, 0.3680128, 0.05003268, 0.9162799)),
    "Jnt.Hand.Thumb1.R":   ("Jnt.Hand.R",          _q(-0.08609306, 0.92688173, -0.34461, 0.12133505)),
    "Jnt.Hand.Thumb2.R":   ("Jnt.Hand.Thumb1.R",   _q(-0.027824994, 0.009158066, 0.019018687, 0.99938995)),
    "Jnt.Hand.Thumb3.R":   ("Jnt.Hand.Thumb2.R",   _q(0.036700875, 0.011498862, -0.0070044207, 0.99923563)),
    "Jnt.Hand.Index1.R":   ("Jnt.Hand.R",          _q(-0.032732613, 0.7167498, 0.004687169, 0.696546)),
    "Jnt.Hand.Index2.R":   ("Jnt.Hand.Index1.R",   _q(-0.03274752, 0.02891066, 0.00017586719, 0.99904543)),
    "Jnt.Hand.Index3.R":   ("Jnt.Hand.Index2.R",   _q(-0.08337469, 0.00647526, -0.0060520517, 0.99647886)),
    "Jnt.Hand.Middle1.R":  ("Jnt.Hand.R",          _q(-0.025618438, 0.7119506, 0.028917044, 0.70116615)),
    "Jnt.Hand.Middle2.R":  ("Jnt.Hand.Middle1.R",  _q(-0.035439707, 0.023255581, 2.1353359e-05, 0.9991012)),
    "Jnt.Hand.Middle3.R":  ("Jnt.Hand.Middle2.R",  _q(-0.121857665, -0.005044027, -0.0032211945, 0.9925296)),
    "Jnt.Hand.Ring1.R":    ("Jnt.Hand.R",          _q(-0.008290685, 0.6606432, 0.04005965, 0.74958456)),
    "Jnt.Hand.Ring2.R":    ("Jnt.Hand.Ring1.R",    _q(-0.034834072, -0.0008354783, 0.0017228764, 0.9993913)),
    "Jnt.Hand.Ring3.R":    ("Jnt.Hand.Ring2.R",    _q(-0.15707456, 0.014415807, 0.0027186028, 0.9874778)),
    "Jnt.Hand.Pinky1.R":   ("Jnt.Hand.PalmCup.R",  _q(-0.0746531, 0.23051944, -0.07255544, 0.96748304)),
    "Jnt.Hand.Pinky2.R":   ("Jnt.Hand.Pinky1.R",   _q(-0.032214664, 0.010016692, -0.001951571, 0.9994289)),
    "Jnt.Hand.Pinky3.R":   ("Jnt.Hand.Pinky2.R",   _q(-0.14490609, 0.0071131433, 0.00207278, 0.9894177)),
}

# Bones to actually realign in Blender, parent-first per side. Shoulder
# is intentionally omitted (we use its quaternion only to compose
# UpperArm's world rotation). Order matters: each call rotates the
# bone's entire subtree rigidly, so children get re-aligned by their own
# entry later in the list.
_TPOSE_UPDATE_ORDER = [
    "Jnt.UpperArm.L", "Jnt.LowerArm.L", "Jnt.Hand.L", "Jnt.Hand.PalmCup.L",
    "Jnt.Hand.Thumb1.L", "Jnt.Hand.Thumb2.L", "Jnt.Hand.Thumb3.L",
    "Jnt.Hand.Index1.L", "Jnt.Hand.Index2.L", "Jnt.Hand.Index3.L",
    "Jnt.Hand.Middle1.L", "Jnt.Hand.Middle2.L", "Jnt.Hand.Middle3.L",
    "Jnt.Hand.Ring1.L", "Jnt.Hand.Ring2.L", "Jnt.Hand.Ring3.L",
    "Jnt.Hand.Pinky1.L", "Jnt.Hand.Pinky2.L", "Jnt.Hand.Pinky3.L",

    "Jnt.UpperArm.R", "Jnt.LowerArm.R", "Jnt.Hand.R", "Jnt.Hand.PalmCup.R",
    "Jnt.Hand.Thumb1.R", "Jnt.Hand.Thumb2.R", "Jnt.Hand.Thumb3.R",
    "Jnt.Hand.Index1.R", "Jnt.Hand.Index2.R", "Jnt.Hand.Index3.R",
    "Jnt.Hand.Middle1.R", "Jnt.Hand.Middle2.R", "Jnt.Hand.Middle3.R",
    "Jnt.Hand.Ring1.R", "Jnt.Hand.Ring2.R", "Jnt.Hand.Ring3.R",
    "Jnt.Hand.Pinky1.R", "Jnt.Hand.Pinky2.R", "Jnt.Hand.Pinky3.R",
]


def _unity_world_rotation(bone_name):
    """Compose ``bone_name``'s Unity-world rotation by walking parent
    pointers up to the root of the recorded chain (where parent is
    ``None``, meaning "the chest is identity"). Returns a Blender
    ``Quaternion``.
    """
    chain = []
    cur = bone_name
    while cur is not None:
        parent, _ = _UNITY_TPOSE[cur]
        chain.append(cur)
        cur = parent
    chain.reverse()
    world = Quaternion((1.0, 0.0, 0.0, 0.0))
    for name in chain:
        _, local = _UNITY_TPOSE[name]
        world = world @ local
    return world


def _unity_to_blender_dir(v):
    """Convert a Unity-world direction (left-handed, Y-up) to a
    Blender-world direction (right-handed, Z-up) for an avatar that
    stands upright in both apps with avatar-left along world +X. The
    handedness flip is absorbed by the y/z axis swap.
    """
    return Vector((v.x, v.z, v.y))


def _world_tail_dir_blender(bone_name):
    """Return the Blender-world direction the bone's tail should point
    in T-pose. Unity humanoid bones point along their local +Y axis, so
    we rotate ``(0, 1, 0)`` by the bone's composed Unity-world rotation
    and convert into Blender's frame.
    """
    return _unity_to_blender_dir(_unity_world_rotation(bone_name) @ Vector((0.0, 1.0, 0.0)))


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


def force_tpose(armature, meshes):
    """Convert the rig's rest pose from Rec Room A-pose to humanoid T-pose.

    Unity humanoid (and the VRChat SDK in particular) calibrates muscle
    space relative to T-pose, so an A-pose rest causes shipped animations
    (claps, dances, etc.) to drive arms tucked into the torso. Each bone
    in the arm subtree (UpperArm down through the fingers, both sides)
    is rotated so its tail points along the Unity-world direction it
    would have after pressing "Enforce T-Pose" in the avatar's Rig
    inspector. Walking the chain parent-first means each bone's
    direction is applied on top of its parent's already-corrected
    orientation, naturally preserving Unity's finger spread and the
    canonical thumb angle that VRChat expects.
    """
    for bone_name in _TPOSE_UPDATE_ORDER:
        target = _world_tail_dir_blender(bone_name)
        _force_tpose_arm(armature, meshes, bone_name, target)

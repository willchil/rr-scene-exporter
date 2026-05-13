"""Small shared helpers (name normalisation + selection management)."""

import re

import bpy


# Blender suffixes imported objects with ``.001``, ``.002``, ... when an object
# with the same name already exists in the file (rigged_reference.blend pre-defines
# Wrist_Watch_*_LOD0 as weight donors, so the GLB's identically-named meshes
# get renamed). Strip that suffix when matching against caller-supplied names.
_DUP_SUFFIX = re.compile(r"\.\d{3}$")


def base_name(name):
    return _DUP_SUFFIX.sub("", name)


def select_only(*objs):
    bpy.ops.object.mode_set(mode='OBJECT')
    bpy.ops.object.select_all(action='DESELECT')
    for o in objs:
        if o is not None:
            o.select_set(True)
    if objs and objs[0] is not None:
        bpy.context.view_layer.objects.active = objs[0]

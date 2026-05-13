"""Rec Room avatar GLB -> rigged FBX conversion pipeline.

Implementation split across several modules for readability; this package's
public surface is just ``main()``, which the sibling ``avatar_convert.py``
launcher invokes after Blender has loaded ``rigged_reference.blend``.
"""

from .main import main

__all__ = ["main"]

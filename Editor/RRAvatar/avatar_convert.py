"""Blender headless entry point: convert a Rec Room avatar GLB into a rigged FBX.

Blender invokes this script via ``--python``. The real implementation lives
in the sibling ``avatar_convert/`` package; this thin shim just adds the
script's directory to ``sys.path`` so the package can be imported, then runs
its entry point.

Usage:
    blender rigged_reference.blend --background --python avatar_convert.py -- input.glb output.fbx
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from avatar_convert import main  # noqa: E402


if __name__ == "__main__":
    main()

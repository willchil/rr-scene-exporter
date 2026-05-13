"""Material fixup: bake (Multiply) tint nodes into base color + diffuse_color.

The Rec Room GLB encodes material tint as a Mix(Multiply) node in front of the
Base Color input. The FBX exporter only writes the Principled BSDF's Base
Color and ``material.diffuse_color`` defaults, so we collapse the Mix into
those two properties (and re-wire the texture directly).
"""

import bpy


def fix_material_tints():
    """
    For each material with a Mix (Multiply) node feeding Base Color:
    1. Read the tint color from the Mix node's second color input (B / input[7]).
    2. Set ``material.diffuse_color`` and the Principled BSDF Base Color default
       to that tint (the FBX exporter writes both).
    3. Rewire: connect the texture directly to Base Color, remove the Mix node.

    Mirrors ``glb_to_fbx.py`` so the FBX exporter carries both texture and tint.
    """
    fixed = 0
    for mat in bpy.data.materials:
        if not mat.node_tree:
            continue

        principled = next((n for n in mat.node_tree.nodes if n.type == "BSDF_PRINCIPLED"), None)
        if principled is None:
            continue

        base_color_input = principled.inputs.get("Base Color")
        if base_color_input is None or not base_color_input.is_linked:
            continue

        mix_node = base_color_input.links[0].from_node
        if mix_node.bl_idname != "ShaderNodeMix":
            continue
        if not hasattr(mix_node, "blend_type") or mix_node.blend_type != "MULTIPLY":
            continue

        tint_input = mix_node.inputs[7]
        if tint_input.type != "RGBA":
            continue
        tint = tuple(tint_input.default_value)

        tex_node = None
        tex_socket = None
        tex_input = mix_node.inputs[6]
        if tex_input.is_linked:
            tex_node = tex_input.links[0].from_node
            tex_socket = tex_input.links[0].from_socket

        mat.diffuse_color = (tint[0], tint[1], tint[2], tint[3])
        base_color_input.default_value = tint

        links = mat.node_tree.links
        for link in list(links):
            if link.to_socket == base_color_input:
                links.remove(link)
        if tex_node and tex_socket:
            links.new(tex_socket, base_color_input)
        mat.node_tree.nodes.remove(mix_node)
        fixed += 1

    print(f"Fixed tint colors on {fixed} materials")
    return fixed

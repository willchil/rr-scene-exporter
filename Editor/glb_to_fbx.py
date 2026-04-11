"""
Blender headless script: Import GLB, fix material tints for FBX export.

Reads the tint color from each material's Mix (Multiply) node, rewires the
node tree so the FBX exporter carries both the texture and the tint, then
exports as FBX.

Usage (headless):
    blender --background --python glb_to_fbx.py -- input.glb output.fbx
"""

import bpy
import sys
import os


def parse_args():
    """Parse arguments after '--' when run headless."""
    if "--" in sys.argv:
        args = sys.argv[sys.argv.index("--") + 1:]
        if len(args) >= 2:
            return args[0], args[1]
        elif len(args) == 1:
            input_glb = args[0]
            output_fbx = os.path.splitext(input_glb)[0] + ".fbx"
            return input_glb, output_fbx
    raise RuntimeError("Usage: blender --background --python glb_to_fbx.py -- input.glb output.fbx")


def fix_material_tints():
    """
    For each material with a Mix (Multiply) node feeding Base Color:
    1. Read the tint color from the Mix node's second color input (B / input[7])
    2. Set material.diffuse_color to the tint
    3. Set the Principled BSDF Base Color default_value to the tint
    4. Rewire: connect the texture directly to Base Color, remove the Mix node

    This ensures the FBX exporter writes both the diffuse color and the texture.
    """
    fixed = 0
    for mat in bpy.data.materials:
        if not mat.node_tree:
            continue

        principled = None
        for node in mat.node_tree.nodes:
            if node.type == "BSDF_PRINCIPLED":
                principled = node
                break
        if not principled:
            continue

        base_color_input = principled.inputs.get("Base Color")
        if not base_color_input or not base_color_input.is_linked:
            continue

        mix_node = base_color_input.links[0].from_node

        if mix_node.bl_idname != "ShaderNodeMix":
            continue
        if not hasattr(mix_node, "blend_type") or mix_node.blend_type != "MULTIPLY":
            continue

        # Extract the tint color from input[7] 'B' (RGBA, not linked)
        tint_input = mix_node.inputs[7]
        if tint_input.type != "RGBA":
            continue
        tint = tuple(tint_input.default_value)

        # Find the texture node connected to the Mix node's input[6] 'A'
        tex_node = None
        tex_socket = None
        tex_input = mix_node.inputs[6]
        if tex_input.is_linked:
            tex_node = tex_input.links[0].from_node
            tex_socket = tex_input.links[0].from_socket

        # Set material.diffuse_color (FBX exporter reads this)
        mat.diffuse_color = (tint[0], tint[1], tint[2], tint[3])

        # Set Principled BSDF Base Color default to the tint
        base_color_input.default_value = tint

        # Rewire: remove Mix node, connect texture directly to Base Color
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


def main():
    input_glb, output_fbx = parse_args()
    print(f"Input:  {input_glb}")
    print(f"Output: {output_fbx}")

    # Clear scene
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)

    for block in bpy.data.meshes:
        if block.users == 0:
            bpy.data.meshes.remove(block)
    for block in bpy.data.materials:
        if block.users == 0:
            bpy.data.materials.remove(block)

    # Import GLB
    print("Importing GLB...")
    bpy.ops.import_scene.gltf(filepath=input_glb)
    print(f"  {len(bpy.data.materials)} materials, {len(bpy.data.objects)} objects")

    # Fix tints
    fix_material_tints()

    # Save packed textures to a folder next to the FBX so Unity can import them directly
    tex_dir = os.path.splitext(output_fbx)[0] + "_Textures"
    os.makedirs(tex_dir, exist_ok=True)
    saved_textures = 0
    for img in bpy.data.images:
        if img.packed_file is None:
            continue
        # Determine extension from format
        ext = ".png"
        if img.file_format in ("JPEG", "JPG"):
            ext = ".jpg"
        elif img.file_format == "TARGA":
            ext = ".tga"
        tex_path = os.path.join(tex_dir, img.name + ext)
        img.unpack(method="REMOVE")
        img.filepath_raw = tex_path
        img.save()
        saved_textures += 1
    print(f"Saved {saved_textures} textures to {tex_dir}")

    # Export FBX with texture paths referencing the saved files
    os.makedirs(os.path.dirname(output_fbx), exist_ok=True)
    print("Exporting FBX...")
    bpy.ops.export_scene.fbx(
        filepath=output_fbx,
        use_selection=False,
        apply_scale_options="FBX_SCALE_ALL",
        path_mode="AUTO",
        colors_type="SRGB",
        bake_anim=False,
    )
    print("Done!")


if __name__ == "__main__":
    main()

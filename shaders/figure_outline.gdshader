// An ink outline for a figure (#394 exploration, the Dreamcast cel look): the mesh drawn again
// inflated along its normals with only its back faces, as a material's next pass. Needs normals,
// which MeshScratch writes only for the lit styles' figures (MeshDetail.High).
shader_type spatial;
render_mode unshaded, cull_front, depth_draw_opaque;

uniform float width = 0.014;
uniform vec4 ink : source_color = vec4(0.06, 0.05, 0.08, 1.0);

void vertex() {
    VERTEX += normalize(NORMAL) * width;
}

void fragment() {
    ALBEDO = ink.rgb;
}

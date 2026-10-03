// The ink outline round a figure in the Cartoon style (#394; StyleKit gives the figure material
// this as its next pass): the mesh drawn again inflated along its normals, back faces only, so a
// dark rim shows round the silhouette. Needs the normals MeshScratch writes for the lit styles'
// figures (MeshDetail.High). A vertex of a part drawn without them in the same mesh (a bike's frame
// under its rider) carries MeshScratch.NoNormal, +Z once turned: it is not inflated, so its
// back faces stay behind its front ones and nothing shows.
shader_type spatial;
render_mode unshaded, cull_front, depth_draw_opaque;

uniform float width = 0.012;
uniform vec4 ink : source_color = vec4(0.06, 0.05, 0.08, 1.0);

void vertex() {
    bool none = abs(NORMAL.z - 1.0) < 1e-4 && abs(NORMAL.x) < 1e-4;
    if (!none) {
        VERTEX += normalize(NORMAL) * width;
    }
}

void fragment() {
    ALBEDO = ink.rgb;
}

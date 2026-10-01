shader_type spatial;
render_mode cull_disabled;

// Realistic trees (prototype #181): an EZ-Tree mesh (assets/proto/trees), one surface for the
// bark and one for the leaf cards, MultiMesh-instanced like every other style's trees and handing
// over to the impostor billboards (real_treefar) per tree by distance, as the other styles do.
global uniform vec3 world_cam_pos;
#include "res://shaders/portal_clip.gdshaderinc"

uniform sampler2D albedo_tex : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool leaves = false;
uniform float alpha_cut = 0.45;
uniform vec2 uv_scale = vec2(1.0);
uniform float tree_near = 80.0;
uniform float tree_fade = 20.0;
// instance colours (ChunkNode.Pack) vary around this; their ratio to it varies each tree's leaves
uniform vec3 tint_ref : source_color = vec3(0.20, 0.32, 0.15);

varying vec3 world_pos;
varying float lod_keep;

void vertex() {
    float d = distance(MODEL_MATRIX[3].xyz, world_cam_pos);
    lod_keep = 1.0 - smoothstep(tree_near, tree_near + tree_fade, d);
    if (lod_keep <= 0.0) {
        VERTEX = vec3(0.0);  // collapsed: the billboard stands here
    }
    world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
    if (portal_clipped(world_pos, CAMERA_POSITION_WORLD)) {
        discard;
    }
    // crossfade with the billboard: a screen-space hash dissolve
    if (lod_keep < 1.0 && lod_keep < fract(sin(dot(floor(FRAGCOORD.xy), vec2(12.9898, 78.233))) * 43758.5453)) {
        discard;
    }
    vec4 t = texture(albedo_tex, UV * uv_scale);
    if (leaves) {
        vec3 vary = clamp(COLOR.rgb / tint_ref, vec3(0.75), vec3(1.3));
        ALBEDO = t.rgb * vary;
        ALPHA = t.a;
        ALPHA_SCISSOR_THRESHOLD = alpha_cut;
        ROUGHNESS = 0.8;
        BACKLIGHT = vec3(0.22, 0.28, 0.10);  // sun through the leaves
    } else {
        ALBEDO = t.rgb;
        ROUGHNESS = 0.95;
    }
}

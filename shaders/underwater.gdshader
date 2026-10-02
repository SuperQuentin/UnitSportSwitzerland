// Underwater (#299): a full-screen pass drawn while the camera is under the water surface
// (World/WaterSurface shows it). Fog and tint by the distance travelled through water: to what is
// behind each pixel, or to the surface above when the ray leaves the water first (the surface is
// transparent, so it is not in the depth buffer). PS1: stepped and dithered like the rest.
shader_type spatial;
render_mode unshaded, cull_disabled, depth_test_disabled, depth_draw_never, blend_mix, fog_disabled, shadows_disabled;

#include "res://shaders/common/world.gdshaderinc"
#include "res://shaders/common/retro.gdshaderinc"

uniform sampler2D scene_depth : hint_depth_texture, filter_nearest;
uniform vec3 water_color : source_color = vec3(0.09, 0.22, 0.25);
// metres of water that take 63 % of the view
uniform float visibility = 14.0;
// the water surface's altitude over the camera, world space (set every frame while shown)
uniform float surface_y = 0.0;

void vertex() {
    // the quad covers the screen whatever the camera does
    POSITION = vec4(VERTEX.xy, 1.0, 1.0);
}

void fragment() {
    float depth = texture(scene_depth, SCREEN_UV).r;
    vec4 view = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, depth, 1.0);
    // reverse Z: 0 is the far plane (sky)
    float dist = depth <= 0.0 ? 1e6 : length(view.xyz / view.w);
    vec3 ray = normalize(view.xyz / view.w);
    vec3 dir = (INV_VIEW_MATRIX * vec4(ray, 0.0)).xyz;
    float under = surface_y - CAMERA_POSITION_WORLD.y;
    if (dir.y > 1e-4) {
        dist = min(dist, max(under, 0.0) / dir.y);
    }
    float fog = 1.0 - exp(-dist / visibility);
    // a little light from above, less the deeper the camera is
    vec3 col = water_color * mix(1.0, 0.45, clamp(under / 25.0, 0.0, 1.0)) * world_tint;
    ALBEDO = retro_posterize(col, FRAGCOORD.xy);
    ALPHA = retro ? floor(clamp(fog, 0.0, 0.97) * 8.0 + bayer4(FRAGCOORD.xy)) / 8.0 : clamp(fog, 0.0, 0.97);
}

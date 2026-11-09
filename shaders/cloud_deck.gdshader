// The cloud deck while it snows (#1643, src/Occasions/OccasionPrecip): a flat slab of fog,
// deck_base..deck_top metres over the camera (negative: under it), drawn as one full-screen pass
// like underwater.gdshader. Each pixel is fogged by the length of its view ray inside the slab, up
// to what the depth buffer holds (the sky: far away), so from under it hides the sky, from over it
// the ground, and inside it everything past a couple of hundred metres. The slab reaches `reach`
// metres round the camera, its edge soft; `strength` (0..1) fades the whole of it.
// World up is the camera's up in every frame the game has (the earth frame re-anchors every 3 km,
// 0.03° at most), and the heights come from WorldOrigin.AltitudeOf, so no world Y is read here.
shader_type spatial;
render_mode unshaded, cull_disabled, depth_test_disabled, depth_draw_never, blend_mix, fog_disabled, shadows_disabled;

#include "res://shaders/common/world.gdshaderinc"

uniform sampler2D scene_depth : hint_depth_texture, filter_nearest;
uniform float deck_base = 1900.0;   // m over the camera
uniform float deck_top = 2400.0;
uniform float strength = 1.0;
uniform float visibility = 70.0;    // metres of cloud that take 63 % of the view
uniform float reach = 40000.0;      // m round the camera, horizontally
uniform vec3 top_color : source_color = vec3(0.93, 0.94, 0.97);
uniform vec3 under_color : source_color = vec3(0.60, 0.63, 0.68);
uniform float indoors_below = -2000.0; // camera heights under this are inside a building

void vertex() {
    // the quad covers the screen whatever the camera does
    POSITION = vec4(VERTEX.xy, 1.0, 1.0);
}

void fragment() {
    if (CAMERA_POSITION_WORLD.y < indoors_below || strength <= 0.0) {
        discard;
    }
    float depth = texture(scene_depth, SCREEN_UV).r;
    vec4 view = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, depth, 1.0);
    // reverse Z: 0 is the far plane (sky)
    float dist = depth <= 0.0 ? 1e7 : length(view.xyz / view.w);
    vec3 ray = normalize(view.xyz / view.w);
    vec3 dir = (INV_VIEW_MATRIX * vec4(ray, 0.0)).xyz;

    // where along the ray it is inside the slab
    float enter;
    float leave;
    if (abs(dir.y) < 1e-5) {
        bool inside = deck_base <= 0.0 && deck_top >= 0.0;
        enter = inside ? 0.0 : 1e9;
        leave = inside ? 1e9 : 0.0;
    } else {
        float a = deck_base / dir.y;
        float b = deck_top / dir.y;
        enter = min(a, b);
        leave = max(a, b);
    }
    // ...and within reach of the camera, which fades the far edge
    float flat_len = sqrt(max(1.0 - dir.y * dir.y, 1e-6));
    float edge = reach / flat_len;
    enter = max(enter, 0.0);
    leave = min(min(leave, dist), edge);
    float len = max(leave - enter, 0.0);
    if (len <= 0.0) {
        discard;
    }
    float fog = 1.0 - exp(-len / visibility);
    // what is seen of it: thinner towards the edge of its reach
    float far = smoothstep(0.6, 1.0, enter * flat_len / reach);
    fog *= strength * (1.0 - far);

    // lit on top, grey underneath: by where the camera is in the slab
    float up = clamp(-deck_base / max(deck_top - deck_base, 1.0), 0.0, 1.0);
    vec3 col = mix(under_color, top_color, up) * mix(world_tint, vec3(1.0), 0.25);
    ALBEDO = col;
    ALPHA = clamp(fog, 0.0, 0.98);
}

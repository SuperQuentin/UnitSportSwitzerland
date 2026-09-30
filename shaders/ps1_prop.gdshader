shader_type spatial;
render_mode unshaded, cull_disabled;

// Time of day, set once per frame for the whole world by World/DayNight: the sun's direction,
// the colour of the light, the sky the fog fades into (linear), and 0 day .. 1 night.
global uniform vec3 world_sun_dir;
global uniform vec3 world_tint;
global uniform vec3 world_sky;
global uniform float world_night;
#include "res://shaders/world_occasion.gdshaderinc"

// Occasion props (src/Occasions/OccasionDecor): jack-o'-lanterns, treat bowls, pumpkins, lights,
// town trees. Same PS1 treatment as the trees — vertex snap, stepped sun, dither, fog — plus a
// glow: vertex-colour ALPHA marks the parts that are a light source. By day those parts are
// darkened (an unlit candle, a switched-off bulb); at night they shine at their own colour through
// the darkness, flickering by the per-instance seed in INSTANCE_CUSTOM.x. INSTANCE_CUSTOM.y scales
// the glow (0 = never lit).

uniform vec2 snap_resolution = vec2(640.0, 480.0);
uniform float color_levels = 16.0;
uniform float fog_start = 3500.0;
uniform float fog_end = 8000.0;
uniform vec3 fog_color : source_color = vec3(0.72, 0.78, 0.86);
// 0 steady (fairy lights), 1 a candle
uniform float flicker = 1.0;

varying vec3 world_pos;
varying float seed;
varying float glow_gain;

void vertex() {
    world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    seed = INSTANCE_CUSTOM.x;
    glow_gain = INSTANCE_CUSTOM.y;
    vec4 clip = PROJECTION_MATRIX * MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
    if (clip.w > 0.0) {
        vec2 half_res = snap_resolution * 0.5;
        vec2 ndc = clip.xy / clip.w;
        ndc = floor(ndc * half_res + 0.5) / half_res;
        clip.xy = ndc * clip.w;
    }
    POSITION = clip;
}

void fragment() {
    vec3 n = normalize(cross(dFdy(world_pos), dFdx(world_pos)));
    float ndl = clamp(dot(n, normalize(world_sun_dir)), 0.0, 1.0);
    float light = floor(ndl * 4.0) / 4.0 * 0.5 + 0.5;

    float mask = COLOR.a;
    // an unlit lamp by day is the dark hollow it really is
    vec3 col = COLOR.rgb * mix(1.0, 0.18, mask) * light;
    col = occasion_snow(col, n, 0.7);
    col *= world_tint;

    float wobble = 0.82 + 0.18 * sin(TIME * (6.0 + seed * 5.0) + seed * 40.0) * sin(TIME * 2.3 + seed * 13.0);
    float glow = mask * glow_gain * world_night * mix(1.0, wobble, flicker);
    col = max(col, COLOR.rgb * glow);

    const float bayer[16] = float[16](
        0.0, 8.0, 2.0, 10.0,
        12.0, 4.0, 14.0, 6.0,
        3.0, 11.0, 1.0, 9.0,
        15.0, 7.0, 13.0, 5.0);
    col = occasion_mist(col, world_pos, length(VERTEX));
    int bi = int(mod(FRAGCOORD.x, 4.0)) + 4 * int(mod(FRAGCOORD.y, 4.0));
    float dith = (bayer[bi] / 16.0 - 0.5) / color_levels;
    col = floor((col + dith) * color_levels + 0.5) / color_levels;

    float fog = smoothstep(fog_start, fog_end, length(VERTEX));
    ALBEDO = mix(col, world_sky, fog);
}

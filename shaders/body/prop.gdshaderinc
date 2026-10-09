// Prop body, shared by every style's prop wrapper (ps1_prop). Each wrapper is shader_type,
// render_mode, its defines, then this include.

#include "res://shaders/common/world.gdshaderinc"
#include "res://shaders/common/retro.gdshaderinc"
#include "res://shaders/common/style.gdshaderinc"
#include "res://shaders/world_occasion.gdshaderinc"
#include "res://shaders/door_light.gdshaderinc"
#include "res://shaders/portal_clip.gdshaderinc"
#include "res://shaders/common/music.gdshaderinc"

// Occasion props (src/Occasions/OccasionDecor): jack-o'-lanterns, treat bowls, pumpkins, lights,
// town trees. Same PS1 treatment as the trees — vertex snap, stepped sun, dither, fog — plus a
// glow: vertex-colour ALPHA marks the parts that are a light source. By day those parts are
// darkened (an unlit candle, a switched-off bulb); at night they shine at their own colour through
// the darkness, flickering by the per-instance seed in INSTANCE_CUSTOM.x. INSTANCE_CUSTOM.y scales
// the glow (0 = never lit).

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
    POSITION = retro_snap(PROJECTION_MATRIX * MODELVIEW_MATRIX * vec4(VERTEX, 1.0));
}

void fragment() {
    if (portal_clipped(world_pos, CAMERA_POSITION_WORLD)) {
        discard;
    }
    vec3 n = facet_normal(VERTEX, INV_VIEW_MATRIX);

    float mask = COLOR.a;
    // an unlit lamp by day is the dark hollow it really is
    vec3 col = COLOR.rgb * mix(1.0, 0.18, mask);
#ifndef STYLE_LIT
    float ndl = clamp(dot(n, normalize(world_sun_dir)), 0.0, 1.0);
    col *= retro_steps(ndl, 4.0) * 0.5 + 0.5;
#endif
    col = occasion_snow(col, n, 0.7);

    float wobble = 0.82 + 0.18 * sin(TIME * (6.0 + seed * 5.0) + seed * 40.0) * sin(TIME * 2.3 + seed * 13.0);
    float glow = mask * glow_gain * world_night * mix(1.0, wobble, flicker);
    // near music (#734) the lights flash with the kicks, by day too
    float mr = music_reach(world_pos);
    if (mr > 0.0) {
        glow = max(glow * (1.0 + 1.2 * world_music_beat.x * mr), mask * glow_gain * mr * (0.15 + 0.85 * world_music_beat.x));
    }
#ifdef STYLE_LIT
    col = style_grade(occasion_mist(col, world_pos, length(VERTEX)));
    ALBEDO = col;
    NORMAL = style_view_normal(VIEW_MATRIX, n);
    ROUGHNESS = 1.0;
    EMISSION = col * door_light(world_pos, n) + COLOR.rgb * glow;
#else
    col *= world_tint + door_light(world_pos, n);
    col = max(col, COLOR.rgb * glow);

    col = occasion_mist(col, world_pos, length(VERTEX));
    col = retro_posterize(col, FRAGCOORD.xy);

    float fog = smoothstep(fog_start, fog_end, length(VERTEX));
    ALBEDO = mix(col, world_sky, fog);
#endif
}

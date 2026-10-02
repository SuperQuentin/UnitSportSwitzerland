// Water body, shared by every style's water wrapper (ps1_water). Each wrapper is shader_type,
// render_mode, its defines, then this include.

#include "res://shaders/common/world.gdshaderinc"
#include "res://shaders/common/retro.gdshaderinc"
#include "res://shaders/common/style.gdshaderinc"
#include "res://shaders/world_occasion.gdshaderinc"
#include "res://shaders/portal_clip.gdshaderinc"
#include "res://shaders/world_origin.gdshaderinc"
#include "res://shaders/common/waves.gdshaderinc"

// Water: banded blue with a slow dithered shimmer. No reflection — PS1-era water was flat
// colour plus animated dither, which is also what reads best against the terrain's own
// posterised palette. The surface moves with the physical waves (#299, common/waves, in
// vertex()); PS1 draws it semitransparent, clearer toward the shore (depth fade), and from
// below as a bright underside (an unlit style wrapper without STYLE_LIT).

uniform float fog_start = 3500.0;
uniform float fog_end = 8000.0;
uniform vec3 fog_color : source_color = vec3(0.72, 0.78, 0.86);
uniform vec3 deep_color : source_color = vec3(0.13, 0.24, 0.36);
uniform vec3 shallow_color : source_color = vec3(0.25, 0.43, 0.52);
uniform vec3 glint_color : source_color = vec3(0.62, 0.76, 0.82);
uniform float wave_speed = 0.35;
#ifndef STYLE_LIT
// semitransparency: alpha at the waterline and through `fade_depth` metres of water and more
uniform float shore_alpha = 0.55;
uniform float deep_alpha = 0.9;
uniform float fade_depth = 4.0;
uniform float below_alpha = 0.8;
uniform vec3 underside_color : source_color = vec3(0.55, 0.72, 0.78);
uniform sampler2D water_depth_texture : hint_depth_texture, filter_nearest;
#endif

varying vec3 world_pos;
// the physical waves at this point: the surface normal and the lift above the still level (#299)
varying vec3 wave_normal;
varying float wave_lift;

void vertex() {
    // UV.x: the water layer's wave scale (fetch x depth). The tile's transform is a translation,
    // so the world displacement is the local one.
    vec3 rest = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    vec3 n;
    vec3 d = water_wave_displace(pattern_xz(rest), UV.x, n);
    wave_normal = n;
    VERTEX += d;
    wave_lift = d.y;
    world_pos = rest + d;
    POSITION = retro_snap(PROJECTION_MATRIX * MODELVIEW_MATRIX * vec4(VERTEX, 1.0));
}

void fragment() {
    if (portal_clipped(world_pos, CAMERA_POSITION_WORLD)) {
        discard;
    }
#ifndef STYLE_LIT
    // PS1 (#299): see-through toward the shore, by the water between this surface and what is
    // behind it; from below (FRONT_FACING false under cull_disabled) a bright, nearly opaque skin
    float scene_depth = texture(water_depth_texture, SCREEN_UV).r;
    vec4 behind = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, scene_depth, 1.0);
    float through = max(0.0, -behind.z / behind.w + VERTEX.z);
    float clear = smoothstep(0.0, fade_depth, through);
    ALPHA = FRONT_FACING ? mix(shore_alpha, deep_alpha, clear) : below_alpha;
    ALPHA = floor(ALPHA * 8.0 + bayer4(FRAGCOORD.xy)) / 8.0;
#endif
    // two drifting wave trains, quantised into bands
    float t = TIME * wave_speed;
    vec2 ground = pattern_xz(world_pos);
    float w = sin(ground.x * 0.35 + t) * 0.5 + sin(ground.y * 0.27 - t * 0.8) * 0.5;
    // the physical waves (#299) light the bands where they are tall enough to see: a slope facing
    // the sun is light, one turned away dark; a calm lake keeps the drifting shimmer alone
    float wave_amount = clamp(abs(wave_lift) * 4.0 + (1.0 - wave_normal.y) * 6.0, 0.0, 1.0);
    vec3 sun = normalize(world_sun_dir);
    w = mix(w, (dot(wave_normal, sun) - sun.y) * 8.0 + wave_lift * 0.6, wave_amount);
    float band = floor((clamp(w, -1.0, 1.0) * 0.5 + 0.5) * 4.0) / 4.0;

    vec3 col = mix(deep_color, shallow_color, band);
#ifndef STYLE_LIT
    // from below: a bright skin, the waves' slopes in its bands
    col = FRONT_FACING ? col : underside_color * (0.5 + 0.7 * band);
#endif

    // sparse glints on the wave crests — kept small and infrequent, otherwise they read
    // as patches of foam rather than light catching the surface
    float crest = step(0.90, w * 0.5 + 0.5);
    float sparkle = step(0.78, fract(sin(dot(floor(ground * 2.3), vec2(12.9898, 78.233))
                                         + floor(TIME * 3.0)) * 43758.5453));
    col = mix(col, glint_color, crest * sparkle * 0.55);

#ifdef STYLE_LIT
    // the two wave trains' slopes as a normal, so the sun catches the ripples, on top of the
    // physical waves' own normal (#299; straight up on still water, as before)
    float dx = cos(ground.x * 0.35 + t) * 0.35 * 0.5;
    float dz = -sin(ground.y * 0.27 - t * 0.8) * 0.27 * 0.5;
    vec3 ripple = normalize(wave_normal + vec3(-dx * 0.6, 0.0, -dz * 0.6));
    NORMAL = style_view_normal(VIEW_MATRIX, ripple);
#ifdef STYLE_REAL
    // dark and glossy: the sky's reflection and the sun's glint do the rest
    ALBEDO = occasion_mist(deep_color * 0.6, (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz, length(VERTEX));
    ROUGHNESS = 0.05;
    SPECULAR = 0.6;
#else
    ALBEDO = style_grade(occasion_mist(col, (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz, length(VERTEX)));
    ROUGHNESS = 0.6;
#endif
#else
    col *= world_tint;
    // valley mist of a running occasion; a no-op on an ordinary day
    col = occasion_mist(col, (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz, length(VERTEX));
    col = retro_posterize(col, FRAGCOORD.xy);

    float fog = smoothstep(fog_start, fog_end, length(VERTEX));
    ALBEDO = mix(col, world_sky, fog);
#endif
}

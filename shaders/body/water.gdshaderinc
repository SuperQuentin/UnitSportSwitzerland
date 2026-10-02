// Water body, shared by every style's water wrapper (ps1_water). Each wrapper is shader_type,
// render_mode, its defines, then this include.

#include "res://shaders/common/world.gdshaderinc"
#include "res://shaders/common/retro.gdshaderinc"
#include "res://shaders/common/style.gdshaderinc"
#include "res://shaders/world_occasion.gdshaderinc"
#include "res://shaders/portal_clip.gdshaderinc"
#include "res://shaders/world_origin.gdshaderinc"

// Water: banded blue with a slow dithered shimmer. No transparency or reflection —
// PS1-era water was flat colour plus animated dither, which is also what reads best
// against the terrain's own posterised palette.

uniform float fog_start = 3500.0;
uniform float fog_end = 8000.0;
uniform vec3 fog_color : source_color = vec3(0.72, 0.78, 0.86);
uniform vec3 deep_color : source_color = vec3(0.13, 0.24, 0.36);
uniform vec3 shallow_color : source_color = vec3(0.25, 0.43, 0.52);
uniform vec3 glint_color : source_color = vec3(0.62, 0.76, 0.82);
uniform float wave_speed = 0.35;

varying vec3 world_pos;

void vertex() {
    world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    POSITION = retro_snap(PROJECTION_MATRIX * MODELVIEW_MATRIX * vec4(VERTEX, 1.0));
}

void fragment() {
    if (portal_clipped(world_pos, CAMERA_POSITION_WORLD)) {
        discard;
    }
    // two drifting wave trains, quantised into bands
    float t = TIME * wave_speed;
    vec2 ground = pattern_xz(world_pos);
    float w = sin(ground.x * 0.35 + t) * 0.5 + sin(ground.y * 0.27 - t * 0.8) * 0.5;
    float band = floor((w * 0.5 + 0.5) * 4.0) / 4.0;

    vec3 col = mix(deep_color, shallow_color, band);

    // sparse glints on the wave crests — kept small and infrequent, otherwise they read
    // as patches of foam rather than light catching the surface
    float crest = step(0.90, w * 0.5 + 0.5);
    float sparkle = step(0.78, fract(sin(dot(floor(ground * 2.3), vec2(12.9898, 78.233))
                                         + floor(TIME * 3.0)) * 43758.5453));
    col = mix(col, glint_color, crest * sparkle * 0.55);

#ifdef STYLE_LIT
    // the two wave trains' slopes as a normal, so the sun catches the ripples
    float dx = cos(ground.x * 0.35 + t) * 0.35 * 0.5;
    float dz = -sin(ground.y * 0.27 - t * 0.8) * 0.27 * 0.5;
    vec3 ripple = normalize(vec3(-dx * 0.6, 1.0, -dz * 0.6));
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

// water body: see shaders/body/style.gdshaderinc for the STYLE_* switches.

// Time of day, set once per frame for the whole world by World/DayNight: the sun's direction,
// the colour of the light, the sky the fog fades into (linear), and 0 day .. 1 night.
global uniform vec3 world_sun_dir;
global uniform vec3 world_tint;
global uniform vec3 world_sky;
global uniform float world_night;
#include "res://shaders/world_occasion.gdshaderinc"
#include "res://shaders/portal_clip.gdshaderinc"
#include "res://shaders/body/style.gdshaderinc"

// Water: banded blue with a slow dithered shimmer. No transparency or reflection —
// PS1-era water was flat colour plus animated dither, which is also what reads best
// against the terrain's own posterised palette.

uniform vec2 snap_resolution = vec2(640.0, 480.0);
uniform float color_levels = 16.0;
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
    vec4 clip = PROJECTION_MATRIX * MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
    POSITION = style_clip(clip, snap_resolution);
}

void fragment() {
    if (portal_clipped(world_pos, CAMERA_POSITION_WORLD)) {
        discard;
    }
    // two drifting wave trains, quantised into bands
    float t = TIME * wave_speed;
    float w = sin(world_pos.x * 0.35 + t) * 0.5 + sin(world_pos.z * 0.27 - t * 0.8) * 0.5;
    float band = floor((w * 0.5 + 0.5) * 4.0) / 4.0;

    vec3 col = mix(deep_color, shallow_color, band);

    // sparse glints on the wave crests — kept small and infrequent, otherwise they read
    // as patches of foam rather than light catching the surface
    float crest = step(0.90, w * 0.5 + 0.5);
    float sparkle = step(0.78, fract(sin(dot(floor(world_pos.xz * 2.3), vec2(12.9898, 78.233))
                                         + floor(TIME * 3.0)) * 43758.5453));
    col = mix(col, glint_color, crest * sparkle * 0.55);

#ifdef STYLE_LIT
    // ripples as a normal: the two wave trains' slopes
    float dx = cos(world_pos.x * 0.35 + t) * 0.35 * 0.5;
    float dz = -sin(world_pos.z * 0.27 - t * 0.8) * 0.27 * 0.5;
    vec3 wn = normalize(vec3(-dx * 0.6, 1.0, -dz * 0.6));
#ifdef STYLE_CARTOON
    ALBEDO = style_cartoon_grade(col);
    ROUGHNESS = 0.6;
#else
    ALBEDO = deep_color * 0.6;
    ROUGHNESS = 0.05;
    METALLIC = 0.0;
    SPECULAR = 0.6;
#endif
    NORMAL = style_view_normal(VIEW_MATRIX, wn);
#else
    col *= world_tint;
    // valley mist of a running occasion; a no-op on an ordinary day
    col = occasion_mist(col, (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz, length(VERTEX));
    col = style_posterize(col, FRAGCOORD.xy, color_levels);

    float fog = smoothstep(fog_start, fog_end, length(VERTEX));
    ALBEDO = mix(col, world_sky, fog);
#endif
}

shader_type spatial;
render_mode unshaded, cull_disabled;

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
    if (clip.w > 0.0) {
        vec2 half_res = snap_resolution * 0.5;
        vec2 ndc = clip.xy / clip.w;
        ndc = floor(ndc * half_res + 0.5) / half_res;
        clip.xy = ndc * clip.w;
    }
    POSITION = clip;
}

void fragment() {
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

    const float bayer[16] = float[16](
        0.0, 8.0, 2.0, 10.0,
        12.0, 4.0, 14.0, 6.0,
        3.0, 11.0, 1.0, 9.0,
        15.0, 7.0, 13.0, 5.0);
    int bi = int(mod(FRAGCOORD.x, 4.0)) + 4 * int(mod(FRAGCOORD.y, 4.0));
    float dith = (bayer[bi] / 16.0 - 0.5) / color_levels;
    col = floor((col + dith) * color_levels + 0.5) / color_levels;

    float fog = smoothstep(fog_start, fog_end, length(VERTEX));
    ALBEDO = mix(col, fog_color, fog);
}

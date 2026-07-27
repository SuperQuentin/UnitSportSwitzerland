shader_type spatial;
render_mode unshaded, cull_disabled;

// The GPX route rendered as a raised path. Flat-shaded from screen derivatives like the
// terrain, so the top surface and the side walls separate and the strip reads as a solid
// 3D object rather than a painted line.

uniform vec2 snap_resolution = vec2(640.0, 480.0);
uniform float color_levels = 16.0;
uniform float fog_start = 3500.0;
uniform float fog_end = 8000.0;
uniform vec3 fog_color : source_color = vec3(0.72, 0.78, 0.86);
uniform vec3 top_color : source_color = vec3(0.98, 0.74, 0.12);
uniform vec3 side_color : source_color = vec3(0.62, 0.42, 0.06);
uniform vec3 sun_direction = vec3(0.45, 0.8, 0.25);

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
    vec3 n = normalize(cross(dFdy(world_pos), dFdx(world_pos)));
    if (n.y < 0.0) {
        n = -n;
    }

    // upward-facing triangles are the tread, steep ones are the kerb
    float flat_face = smoothstep(0.45, 0.85, n.y);
    vec3 col = mix(side_color, top_color, flat_face);

    float ndl = clamp(dot(n, normalize(sun_direction)), 0.0, 1.0);
    col *= floor(ndl * 4.0) / 4.0 * 0.35 + 0.7;

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

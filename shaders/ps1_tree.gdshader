shader_type spatial;
render_mode unshaded, cull_disabled;

// Trees are MultiMesh instances sharing one cone; colour comes from the per-instance
// colour, with the same snap/dither/fog treatment as everything else.

uniform vec2 snap_resolution = vec2(640.0, 480.0);
uniform float color_levels = 16.0;
uniform float fog_start = 3500.0;
uniform float fog_end = 8000.0;
uniform vec3 fog_color : source_color = vec3(0.72, 0.78, 0.86);

varying vec3 world_pos;

// --- sightline cut ----------------------------------------------------------------------
// Dissolves anything standing between a camera and its subject. The fade is a Bayer-dither
// discard, not alpha blending: these shaders are unshaded and opaque, the trees are one
// MultiMesh sharing a single material, and a dithered dissolve is already this renderer's
// visual language. cut_radius = 0 switches the whole thing off.
uniform vec3 cut_from = vec3(0.0);
uniform vec3 cut_to = vec3(0.0);
uniform float cut_radius = 0.0;
uniform float cut_softness = 1.6;

// How much of this fragment survives: 1 fully drawn, 0 fully dissolved.
float sightline_keep(vec3 p) {
    if (cut_radius <= 0.0) {
        return 1.0;
    }
    vec3 axis = cut_to - cut_from;
    float len2 = dot(axis, axis);
    if (len2 < 1.0) {
        return 1.0;
    }

    // where along the camera -> subject segment this fragment sits
    float t = dot(p - cut_from, axis) / len2;
    if (t <= 0.0 || t >= 1.0) {
        return 1.0;
    }

    float radial = length(p - (cut_from + axis * t));

    // Taper to nothing at both ends, so the runner's own feet and the ground immediately under
    // the lens are never eaten - only what is genuinely in between.
    float ends = smoothstep(0.0, 0.12, t) * smoothstep(0.0, 0.12, 1.0 - t);
    float core = 1.0 - smoothstep(cut_radius, cut_radius + cut_softness, radial);

    return 1.0 - core * ends;
}


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
    float ndl = clamp(dot(n, normalize(vec3(0.45, 0.8, 0.25))), 0.0, 1.0);
    float light = floor(ndl * 3.0) / 3.0 * 0.45 + 0.6;
    vec3 col = COLOR.rgb * light;

    const float bayer[16] = float[16](
        0.0, 8.0, 2.0, 10.0,
        12.0, 4.0, 14.0, 6.0,
        3.0, 11.0, 1.0, 9.0,
        15.0, 7.0, 13.0, 5.0);

    // dissolve anything between the camera and its subject, using the same 4x4 Bayer pattern
    // the colour quantiser below uses, so the fade reads as part of the renderer
    float keep = sightline_keep(world_pos);
    if (keep < 1.0) {
        int ci = int(mod(FRAGCOORD.x, 4.0)) + 4 * int(mod(FRAGCOORD.y, 4.0));
        if (keep < bayer[ci] / 16.0) {
            discard;
        }
    }

    int bi = int(mod(FRAGCOORD.x, 4.0)) + 4 * int(mod(FRAGCOORD.y, 4.0));
    float dith = (bayer[bi] / 16.0 - 0.5) / color_levels;
    col = floor((col + dith) * color_levels + 0.5) / color_levels;

    float fog = smoothstep(fog_start, fog_end, length(VERTEX));
    ALBEDO = mix(col, fog_color, fog);
}

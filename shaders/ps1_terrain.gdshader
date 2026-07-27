shader_type spatial;
render_mode unshaded, cull_disabled;

// PS1-style terrain: clip-space vertex snapping, flat shading from screen derivatives,
// altitude/slope palette bands, quantized sun, Bayer-dithered posterization, distance fog.

uniform vec2 snap_resolution = vec2(640.0, 480.0);
uniform vec3 sun_direction = vec3(0.45, 0.8, 0.25);
uniform float light_levels = 5.0;
uniform float color_levels = 16.0;
uniform float fog_start = 3500.0;
uniform float fog_end = 8000.0;
uniform vec3 fog_color : source_color = vec3(0.72, 0.78, 0.86);
uniform vec3 bay_color : source_color = vec3(0.82, 0.81, 0.76);
uniform float bay_width = 2.5;    // Swiss standard bay is 2.5 m x 5.0 m
uniform float bay_depth = 5.0;
uniform float vine_row_spacing = 2.0;  // typical Valais row spacing
uniform vec3 vine_color : source_color = vec3(0.22, 0.32, 0.15);
uniform float mow_stripe = 6.0;


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

    vec3 col = COLOR.rgb;

    // Surface patterns. COLOR.a carries a pattern code in quarter steps (CoverPalette /
    // SurfacePattern). All of them run on world axes rather than the feature's true
    // orientation, which TLM does not record — and which cannot be recovered from the
    // screen-space normal without turning to confetti. Each fades out before it aliases,
    // the same way the road markings do.
    int pattern = int(COLOR.a * 4.0 + 0.5);
    if (pattern > 0) {
        float detail = max(fwidth(world_pos.x), fwidth(world_pos.z));

        if (pattern == 1) {                       // parking bays
            float legible = clamp(1.0 - detail / 0.6, 0.0, 1.0);
            if (legible > 0.0) {
                vec2 cell = vec2(world_pos.x / bay_width, world_pos.z / bay_depth);
                vec2 f = abs(fract(cell) - 0.5);
                // paint the bay divider lines, leaving the aisle ends open
                float line = max(step(0.46, f.x), step(0.47, f.y) * step(0.25, f.x));
                col = mix(col, bay_color, line * legible * 0.85);
            }
        } else if (pattern == 2) {                // vine rows
            float legible = clamp(1.0 - detail / 0.5, 0.0, 1.0);
            if (legible > 0.0) {
                float f = abs(fract(world_pos.x / vine_row_spacing) - 0.5);
                col = mix(col, vine_color, smoothstep(0.20, 0.42, f) * legible * 0.75);
            }
        } else if (pattern == 3) {                // mown stripes on turf
            float legible = clamp(1.0 - detail / 1.5, 0.0, 1.0);
            float band = step(0.5, fract(world_pos.z / mow_stripe));
            col *= mix(1.0, mix(0.92, 1.08, band), legible);
        }
    }

    // steep faces read as bare rock whatever the mapped cover says
    float rockiness = smoothstep(0.62, 0.38, n.y);
    col = mix(col, vec3(0.30, 0.28, 0.26), rockiness * 0.75);

    float ndl = clamp(dot(n, normalize(sun_direction)), 0.0, 1.0);
    // cliffs get a harder light ramp so faceting reads as rock rather than soft ground
    float levels = mix(light_levels, 3.0, rockiness);
    float light = floor(ndl * levels) / levels * mix(0.55, 0.75, rockiness) + mix(0.45, 0.25, rockiness);
    col *= light;

    // 4x4 Bayer dither + posterize
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

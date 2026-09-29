shader_type spatial;
render_mode unshaded;

// Time of day, set once per frame for the whole world by World/DayNight: the sun's direction,
// the colour of the light, the sky the fog fades into (linear), and 0 day .. 1 night.
global uniform vec3 world_sun_dir;
global uniform vec3 world_tint;
global uniform vec3 world_sky;
global uniform float world_night;

// Buildings take their base colour from baked vertex colours (kind + roof/wall + age)
// and get the same PS1 treatment as the terrain: vertex snap, quantised sun from a
// screen-derivative flat normal, Bayer dither, distance fog.

uniform vec2 snap_resolution = vec2(640.0, 480.0);
uniform float light_levels = 5.0;
uniform float color_levels = 16.0;
uniform float fog_start = 3500.0;
uniform float fog_end = 8000.0;
uniform vec3 fog_color : source_color = vec3(0.72, 0.78, 0.86);

uniform vec3 window_color : source_color = vec3(0.16, 0.19, 0.24);
uniform vec3 window_lit : source_color = vec3(0.95, 0.82, 0.52);
uniform float window_spacing = 3.2;

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

varying vec2 facade;   // x = metres along the facade, y = storey coordinate (<0 = none)
varying float storey_count; // whole storeys in the wall; grid stops below this

void vertex() {
    world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    facade = UV;
    storey_count = UV2.x;
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
    // faces are not consistently wound in the source TINs, so orient toward the viewer
    if (!FRONT_FACING) {
        n = -n;
    }

    vec3 col = COLOR.rgb;

    // --- windows -------------------------------------------------------------------
    // Storey height comes from the GWR floor count (baked into UV.y; 0 opts out, and
    // roof faces are always 0). The horizontal axis is derived from the wall's own
    // normal so the grid runs along each facade instead of along world axes.
    // Drop the pattern once a storey is thinner than a couple of pixels, otherwise it
    // aliases into confetti at the low internal render resolution.
    float detail = fwidth(facade.y);
    // stop a little below the wall plate so the eave never cuts a row in half
    float top = storey_count - 0.22;
    vec3 glow = vec3(0.0);
    if (facade.y > 0.0 && facade.y < top && detail < 0.6) {
        float u = facade.x;
        float wall_v = fract(facade.y);
        float col_u = fract(u / window_spacing);
        bool in_row = wall_v > 0.32 && wall_v < 0.76;
        bool in_col = col_u > 0.30 && col_u < 0.70;

        if (facade.y > 0.35 && in_row && in_col) {
            // stable per-window hash decides which ones are lit
            vec2 cell = vec2(floor(u / window_spacing), floor(facade.y));
            float r = fract(sin(dot(cell, vec2(12.9898, 78.233))) * 43758.5453);
            // a few lit windows by day, most of a village at night
            bool lit = r > mix(0.86, 0.35, world_night);
            vec3 win = lit ? window_lit : window_color;
            // fade the pattern out as it approaches the resolution limit
            float legible = clamp(1.0 - detail / 0.6, 0.0, 1.0);
            col = mix(col, win, legible);
            if (lit) glow = window_lit * legible;
        }
    }

    float ndl = clamp(dot(n, normalize(world_sun_dir)), 0.0, 1.0);
    float light = floor(ndl * light_levels) / light_levels * 0.5 + 0.5;
    col *= light;

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

    col *= world_tint;
    // a lit window is its own light: at night it shines through the darkness around it
    col = max(col, glow * world_night);
    int bi = int(mod(FRAGCOORD.x, 4.0)) + 4 * int(mod(FRAGCOORD.y, 4.0));
    float dith = (bayer[bi] / 16.0 - 0.5) / color_levels;
    col = floor((col + dith) * color_levels + 0.5) / color_levels;

    float fog = smoothstep(fog_start, fog_end, length(VERTEX));
    ALBEDO = mix(col, world_sky, fog);
}

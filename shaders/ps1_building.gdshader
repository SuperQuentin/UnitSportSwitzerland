shader_type spatial;
render_mode unshaded;

// Buildings take their base colour from baked vertex colours (kind + roof/wall + age)
// and get the same PS1 treatment as the terrain: vertex snap, quantised sun from a
// screen-derivative flat normal, Bayer dither, distance fog.

uniform vec2 snap_resolution = vec2(640.0, 480.0);
uniform vec3 sun_direction = vec3(0.45, 0.8, 0.25);
uniform float light_levels = 5.0;
uniform float color_levels = 16.0;
uniform float fog_start = 3500.0;
uniform float fog_end = 8000.0;
uniform vec3 fog_color : source_color = vec3(0.72, 0.78, 0.86);

uniform vec3 window_color : source_color = vec3(0.16, 0.19, 0.24);
uniform vec3 window_lit : source_color = vec3(0.95, 0.82, 0.52);
uniform float window_spacing = 3.2;

varying vec3 world_pos;
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
            vec3 win = r > 0.86 ? window_lit : window_color;
            // fade the pattern out as it approaches the resolution limit
            col = mix(col, win, clamp(1.0 - detail / 0.6, 0.0, 1.0));
        }
    }

    float ndl = clamp(dot(n, normalize(sun_direction)), 0.0, 1.0);
    float light = floor(ndl * light_levels) / light_levels * 0.5 + 0.5;
    col *= light;

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

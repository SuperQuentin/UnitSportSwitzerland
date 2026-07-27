shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_opaque;

// Roads share the terrain's PS1 treatment (vertex snap, dither, fog) and take their base
// colour from baked vertex colours, so one material covers every road class in a tile.
//
// Lane markings are NOT in swissTLM3D — Switzerland publishes no marking dataset — so
// they are inferred at build time from width class, surface and whether the carriageway
// is direction-separated, then drawn here from the baked facade coordinates:
//   uv  = (metres along the line, lateral position across it in [-1,1])
//   uv2 = (style id, unused)

uniform vec2 snap_resolution = vec2(640.0, 480.0);
uniform float color_levels = 16.0;
uniform float fog_start = 3500.0;
uniform float fog_end = 8000.0;
uniform vec3 fog_color : source_color = vec3(0.72, 0.78, 0.86);
uniform vec3 marking_color : source_color = vec3(0.88, 0.87, 0.82);
uniform vec3 sleeper_color : source_color = vec3(0.26, 0.21, 0.16);

varying vec2 surf;   // along, lateral
varying float style;

void vertex() {
    surf = UV;
    style = UV2.x;
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
    vec3 col = COLOR.rgb;
    int s = int(style + 0.5);
    float along = surf.x;
    float lat = surf.y;

    // Fade fine detail out before it drops below a pixel, otherwise it aliases badly at
    // the reduced internal render resolution.
    float detail = fwidth(lat);
    float legible = clamp(1.0 - detail / 0.5, 0.0, 1.0);

    if (s == 4) {
        // sleepers across the ballast, roughly prototypical 0.65 m spacing
        if (fract(along / 0.65) < 0.45) {
            col = mix(col, sleeper_color, legible);
        }
    } else if (s > 0 && s < 4 && legible > 0.0) {
        float a = abs(lat);
        // Swiss centre dash cadence: about 6 m of line to 3 m of gap
        bool dash_on = fract(along / 9.0) < 0.66;
        bool paint = false;

        if (s == 1) {
            paint = a < 0.10 && dash_on;                 // dashed centre line
        } else if (s == 2) {
            paint = a > 0.80 && a < 0.93;                // edge lines only
        } else {
            paint = (a > 0.82 && a < 0.94)               // motorway edge lines
                 || (a < 0.08 && dash_on);               // plus a dashed lane divider
        }

        if (paint) {
            col = mix(col, marking_color, legible);
        }
    }

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

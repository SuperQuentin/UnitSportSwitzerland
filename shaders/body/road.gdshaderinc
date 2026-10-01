// road body: see shaders/body/style.gdshaderinc for the STYLE_* switches.

// Time of day, set once per frame for the whole world by World/DayNight: the sun's direction,
// the colour of the light, the sky the fog fades into (linear), and 0 day .. 1 night.
global uniform vec3 world_sun_dir;
global uniform vec3 world_tint;
global uniform vec3 world_sky;
global uniform float world_night;
#include "res://shaders/world_occasion.gdshaderinc"
#include "res://shaders/door_light.gdshaderinc"
#include "res://shaders/portal_clip.gdshaderinc"
#include "res://shaders/body/style.gdshaderinc"

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
varying vec3 world_pos;
#ifdef STYLE_REAL
uniform sampler2D tex_asphalt : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D tex_gravel : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
#endif

void vertex() {
    world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    surf = UV;
    style = UV2.x;
    vec4 clip = PROJECTION_MATRIX * MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
    POSITION = style_clip(clip, snap_resolution);
}

void fragment() {
    if (portal_clipped(world_pos, CAMERA_POSITION_WORLD)) {
        discard;
    }
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

    // Snow (a running occasion): carriageways are ploughed, so it lies only along the edges;
    // ballast holds it between the sleepers.
    col = occasion_snow_amount(col, s == 4 ? 0.65 : smoothstep(0.7, 1.0, abs(lat)) * 0.85 + 0.08);

#ifdef STYLE_LIT
    vec3 up = vec3(0.0, 1.0, 0.0);
#ifdef STYLE_REAL
    {
        // the paint stays the baked/marking colour; the carriageway gets asphalt (ballast: gravel)
        vec2 uv = world_pos.xz / 3.0;
        vec3 surface = s == 4 ? style_tinted(tex_gravel, uv, col, 0.3)
                              : style_tinted(tex_asphalt, uv, col, 0.25);
        float paint_mask = 1.0 - smoothstep(0.0, 0.08, distance(col, marking_color));
        col = mix(surface, col, paint_mask);
        ROUGHNESS = mix(0.85, 0.6, paint_mask);
    }
#endif
#ifdef STYLE_CARTOON
    col = style_cartoon_grade(col);
    ROUGHNESS = 1.0;
#endif
    ALBEDO = col;
    NORMAL = style_view_normal(VIEW_MATRIX, up);
    EMISSION = col * door_light(world_pos, up);
#else
    col *= world_tint + door_light(world_pos, vec3(0.0, 1.0, 0.0));
    // valley mist of a running occasion; a no-op on an ordinary day
    col = occasion_mist(col, (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz, length(VERTEX));
    col = style_posterize(col, FRAGCOORD.xy, color_levels);

    float fog = smoothstep(fog_start, fog_end, length(VERTEX));
    ALBEDO = mix(col, world_sky, fog);
#endif
}

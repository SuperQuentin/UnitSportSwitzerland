// Road body, shared by every style's road wrapper (ps1_road). Each wrapper is shader_type,
// render_mode, its defines, then this include.

#include "res://shaders/common/world.gdshaderinc"
#include "res://shaders/common/retro.gdshaderinc"
#include "res://shaders/common/style.gdshaderinc"
#include "res://shaders/world_occasion.gdshaderinc"
#include "res://shaders/door_light.gdshaderinc"
#include "res://shaders/portal_clip.gdshaderinc"
#include "res://shaders/world_origin.gdshaderinc"

// Roads share the terrain's PS1 treatment (vertex snap, dither, fog) and take their base
// colour from baked vertex colours, so one material covers every road class in a tile.
//
// Lane markings are NOT in swissTLM3D — Switzerland publishes no marking dataset — so
// they are inferred at build time from width class, surface and whether the carriageway
// is direction-separated, then drawn here from the baked facade coordinates:
//   uv  = (metres along the line, lateral position across it in [-1,1])
//   uv2 = (style id, unused)
// A v3 tile (network stage) carries its markings as paint geometry instead (#116): style 6, a
// second surface, colour from the vertex, uv = (along, lateral across the line in [-1,1]).

uniform float fog_start = 3500.0;
uniform float fog_end = 8000.0;
uniform vec3 fog_color : source_color = vec3(0.72, 0.78, 0.86);
uniform vec3 marking_color : source_color = vec3(0.88, 0.87, 0.82);
uniform vec3 sleeper_color : source_color = vec3(0.26, 0.21, 0.16);
// Paint is pulled this fraction of its distance toward the camera, so it wins the depth test
// over the carriageway it lies on at any range, snapped vertices and all.
uniform float paint_depth_bias = 0.002;

#ifdef STYLE_REAL
uniform sampler2D tex_asphalt : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D tex_gravel : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
#endif

varying vec2 surf;   // along, lateral
varying float style;
varying vec3 world_pos;

void vertex() {
    world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    surf = UV;
    style = UV2.x;
    vec4 view = MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
    if (int(UV2.x + 0.5) == 6) {
        view.xyz *= 1.0 - paint_depth_bias;   // toward the eye: same pixel, nearer depth
    }
    POSITION = retro_snap(PROJECTION_MATRIX * view);
}

void fragment() {
    if (portal_clipped(world_pos, CAMERA_POSITION_WORLD)) {
        discard;
    }
    vec3 col = COLOR.rgb;
    int s = int(style + 0.5);
#ifdef STYLE_REAL
    // the carriageway asphalt, the ballast gravel; paint (style 6) and the markings drawn below
    // keep their own colour
    if (s != 6) {
        vec2 ruv = pattern_xz(world_pos) / 3.0;
        col = s == 4 ? style_tinted(tex_gravel, ruv, col, 0.3) : style_tinted(tex_asphalt, ruv, col, 0.25);
    }
#endif
    float along = surf.x;
    float lat = surf.y;

    // Fade fine detail out before it drops below a pixel, otherwise it aliases badly at
    // the reduced internal render resolution.
    float detail = fwidth(lat);
    float legible = clamp(1.0 - detail / 0.5, 0.0, 1.0);

    if (s == 6) {
        // Paint: a line under ~1.5 px dissolves (ordered dither) into the road under it instead
        // of aliasing into broken dots. Triangle paint has a constant lat, so it always draws.
        // Anti-aliasing, not the PS1 finish: it keeps the dither in every style.
        float px = 2.0 / max(fwidth(lat), 1e-4);
        if (clamp(px - 0.5, 0.0, 1.0) < bayer4(FRAGCOORD.xy) + 0.5 / 16.0) {
            discard;
        }
    } else if (s == 4) {
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
    col = occasion_snow_amount(col, s == 4 ? 0.65 : s == 6 ? 0.3 : smoothstep(0.7, 1.0, abs(lat)) * 0.85 + 0.08);

#ifdef STYLE_LIT
    // the facet, toward the eye: carriageways face up, the walls and railings of the network
    // stage stand upright and are lit as such
    vec3 n = normalize(cross(dFdy(world_pos), dFdx(world_pos)));
    if (dot(n, CAMERA_POSITION_WORLD - world_pos) < 0.0) {
        n = -n;
    }
    // valley mist of a running occasion; a no-op on an ordinary day
    col = style_grade(occasion_mist(col, (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz, length(VERTEX)));
    ALBEDO = col;
    NORMAL = style_view_normal(VIEW_MATRIX, n);
#ifdef STYLE_REAL
    ROUGHNESS = s == 6 ? 0.6 : 0.85;
#else
    ROUGHNESS = 1.0;
#endif
    EMISSION = col * door_light(world_pos, vec3(0.0, 1.0, 0.0));
#else
    col *= world_tint + door_light(world_pos, vec3(0.0, 1.0, 0.0));
    // valley mist of a running occasion; a no-op on an ordinary day
    col = occasion_mist(col, (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz, length(VERTEX));
    col = retro_posterize(col, FRAGCOORD.xy);

    float fog = smoothstep(fog_start, fog_end, length(VERTEX));
    ALBEDO = mix(col, world_sky, fog);
#endif
}

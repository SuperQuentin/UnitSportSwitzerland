shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_opaque;

// Time of day, set once per frame for the whole world by World/DayNight: the sun's direction,
// the colour of the light, the sky the fog fades into (linear), and 0 day .. 1 night.
global uniform vec3 world_sun_dir;
global uniform vec3 world_tint;
global uniform vec3 world_sky;
global uniform float world_night;
#include "res://shaders/world_occasion.gdshaderinc"
#include "res://shaders/door_light.gdshaderinc"
#include "res://shaders/portal_clip.gdshaderinc"

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

uniform vec2 snap_resolution = vec2(640.0, 480.0);
uniform float color_levels = 16.0;
uniform float fog_start = 3500.0;
uniform float fog_end = 8000.0;
uniform vec3 fog_color : source_color = vec3(0.72, 0.78, 0.86);
uniform vec3 marking_color : source_color = vec3(0.88, 0.87, 0.82);
uniform vec3 sleeper_color : source_color = vec3(0.26, 0.21, 0.16);
// Paint is pulled this fraction of its distance toward the camera, so it wins the depth test
// over the carriageway it lies on at any range, snapped vertices and all.
uniform float paint_depth_bias = 0.002;
// Past the rings whose terrain is blended under the roads (stride <= 4), roads are still drawn on
// coarse terrain that was never cut for them, and a road at the ground (#119: in town a kerb under
// it) would sink into it. Past far_lift_start the whole road mesh is pulled this many metres per
// metre toward the eye, up to far_lift_max: it wins the depth test against the ground it lies on.
// The whole road mesh (ribbons, sidewalks, rails) is pulled this fraction of its distance toward
// the eye: with roads at the ground (#119) the decimated terrain of the stride-2/4 rings lies in
// the road's plane beside it, and only a bias that grows with distance wins that depth test.
uniform float road_depth_bias = 0.0004;
uniform float far_lift_start = 800.0;
uniform float far_lift_rate = 0.001;
uniform float far_lift_max = 2.0;

varying vec2 surf;   // along, lateral
varying float style;
varying vec3 world_pos;

void vertex() {
    world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    surf = UV;
    style = UV2.x;
    vec4 view = MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
    float dist = length(view.xyz);
    float lift = clamp((dist - far_lift_start) * far_lift_rate, 0.0, far_lift_max);
    view.xyz *= 1.0 - lift / max(dist, 1.0) - road_depth_bias;
    if (int(UV2.x + 0.5) == 6) {
        view.xyz *= 1.0 - paint_depth_bias;   // toward the eye: same pixel, nearer depth
    }
    vec4 clip = PROJECTION_MATRIX * view;
    if (clip.w > 0.0) {
        vec2 half_res = snap_resolution * 0.5;
        vec2 ndc = clip.xy / clip.w;
        ndc = floor(ndc * half_res + 0.5) / half_res;
        clip.xy = ndc * clip.w;
    }
    POSITION = clip;
}

void fragment() {
    if (portal_clipped(world_pos, CAMERA_POSITION_WORLD)) {
        discard;
    }
    vec3 col = COLOR.rgb;
    int s = int(style + 0.5);
    float along = surf.x;
    float lat = surf.y;
    const float bayer[16] = float[16](
        0.0, 8.0, 2.0, 10.0,
        12.0, 4.0, 14.0, 6.0,
        3.0, 11.0, 1.0, 9.0,
        15.0, 7.0, 13.0, 5.0);
    int bi = int(mod(FRAGCOORD.x, 4.0)) + 4 * int(mod(FRAGCOORD.y, 4.0));

    // Fade fine detail out before it drops below a pixel, otherwise it aliases badly at
    // the reduced internal render resolution.
    float detail = fwidth(lat);
    float legible = clamp(1.0 - detail / 0.5, 0.0, 1.0);

    if (s == 6) {
        // Paint: a line under ~1.5 px dissolves (ordered dither) into the road under it instead
        // of aliasing into broken dots. Triangle paint has a constant lat, so it always draws.
        float px = 2.0 / max(fwidth(lat), 1e-4);
        if (clamp(px - 0.5, 0.0, 1.0) < (bayer[bi] + 0.5) / 16.0) {
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

    col *= world_tint + door_light(world_pos, vec3(0.0, 1.0, 0.0));
    // valley mist of a running occasion; a no-op on an ordinary day
    col = occasion_mist(col, (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz, length(VERTEX));
    float dith = (bayer[bi] / 16.0 - 0.5) / color_levels;
    col = floor((col + dith) * color_levels + 0.5) / color_levels;

    float fog = smoothstep(fog_start, fog_end, length(VERTEX));
    ALBEDO = mix(col, world_sky, fog);
}

// Terrain body, shared by every style's terrain wrapper (ps1_terrain) and the far horizon's
// material. Each wrapper is shader_type, render_mode, its defines, then this include.

#include "res://shaders/common/world.gdshaderinc"
#include "res://shaders/common/retro.gdshaderinc"
#include "res://shaders/common/style.gdshaderinc"
#include "res://shaders/world_occasion.gdshaderinc"
#include "res://shaders/door_light.gdshaderinc"
#include "res://shaders/portal_clip.gdshaderinc"
#include "res://shaders/world_origin.gdshaderinc"

// PS1-style terrain: clip-space vertex snapping, flat shading from screen derivatives,
// altitude/slope palette bands, quantized sun, Bayer-dithered posterization, distance fog.

uniform float light_levels = 5.0;
uniform float fog_start = 3500.0;
uniform float fog_end = 8000.0;
uniform vec3 fog_color : source_color = vec3(0.72, 0.78, 0.86);
uniform vec3 bay_color : source_color = vec3(0.82, 0.81, 0.76);
uniform float bay_width = 2.5;    // Swiss standard bay is 2.5 m x 5.0 m
uniform float bay_depth = 5.0;
uniform float vine_row_spacing = 2.0;  // typical Valais row spacing
uniform vec3 vine_color : source_color = vec3(0.22, 0.32, 0.15);
uniform float mow_stripe = 6.0;
// The far-horizon material only: one texel per km tile, 1 where a real streamed tile is on
// screen, so the 100 m lattice is dropped exactly there and nowhere else - never through a
// tunnel floor or a carved portal, and never leaving a hole where a tile has not arrived yet.
// Off by default, so tile materials never pay for it.
uniform bool use_cover = false;
uniform sampler2D detail_cover : filter_nearest, repeat_disable;
uniform vec2 cover_origin = vec2(0.0);   // world XZ of the texture's NW corner
uniform vec2 cover_extent = vec2(1.0);   // world metres the texture spans


#ifdef STYLE_REAL
// CC0 ground sets (assets/realistic/textures, Styles/StyleKit), picked by land cover
uniform sampler2D tex_grass : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D tex_forest : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D tex_gravel : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D tex_rock : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D tex_snow : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D nrm_grass : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D nrm_forest : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D nrm_gravel : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform float tex_scale = 4.0;          // metres per repeat, near
uniform float tex_color_weight = 0.35;  // how much of the texture's own colour shows
#endif

varying vec3 world_pos;

void vertex() {
    world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    POSITION = retro_snap(PROJECTION_MATRIX * MODELVIEW_MATRIX * vec4(VERTEX, 1.0));
}


void fragment() {
    if (portal_clipped(world_pos, CAMERA_POSITION_WORLD)) {
        discard;
    }
    if (use_cover) {
        vec2 uv = (world_pos.xz - cover_origin) / cover_extent;
        if (uv == clamp(uv, vec2(0.0), vec2(1.0)) && texture(detail_cover, uv).r > 0.5) {
            discard;
        }
    }
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
        vec2 ground = pattern_xz(world_pos);

        if (pattern == 1) {                       // parking bays
            float legible = clamp(1.0 - detail / 0.6, 0.0, 1.0);
            if (legible > 0.0) {
                vec2 cell = vec2(ground.x / bay_width, ground.y / bay_depth);
                vec2 f = abs(fract(cell) - 0.5);
                // paint the bay divider lines, leaving the aisle ends open
                float line = max(step(0.46, f.x), step(0.47, f.y) * step(0.25, f.x));
                col = mix(col, bay_color, line * legible * 0.85);
            }
        } else if (pattern == 2) {                // vine rows
            float legible = clamp(1.0 - detail / 0.5, 0.0, 1.0);
            if (legible > 0.0) {
                float f = abs(fract(ground.x / vine_row_spacing) - 0.5);
                col = mix(col, vine_color, smoothstep(0.20, 0.42, f) * legible * 0.75);
            }
        } else if (pattern == 3) {                // mown stripes on turf
            float legible = clamp(1.0 - detail / 1.5, 0.0, 1.0);
            float band = step(0.5, fract(ground.y / mow_stripe));
            col *= mix(1.0, mix(0.92, 1.08, band), legible);
        }
    }

    // a running occasion's snow lies on whatever faces up; the rock test below still bares cliffs
    col = occasion_snow(col, n, 1.0);

    // steep faces read as bare rock whatever the mapped cover says
    float rockiness = smoothstep(0.62, 0.38, n.y);
#ifdef STYLE_REAL
    {
        // the cover family from the baked colour (CoverPalette): grey = rock, scree and paved;
        // dark green = forest floor; very bright = snow; anything else = grass. Farmland and
        // vineyards are only faintly green (0.02-0.04), so grey starts below that.
        vec3 srgb = pow(clamp(col, 0.0, 1.0), vec3(1.0 / 2.2));
        float green = srgb.g - max(srgb.r, srgb.b);
        float luma = dot(srgb, vec3(0.333));
        float w_snow = smoothstep(0.72, 0.80, luma);
        float w_grey = (1.0 - smoothstep(-0.01, 0.015, green)) * (1.0 - w_snow);
        float w_forest = smoothstep(0.08, 0.12, green) * (1.0 - smoothstep(0.26, 0.32, luma));
        float w_grass = max(0.0, 1.0 - w_grey - w_forest - w_snow);
        vec2 ground = pattern_xz(world_pos);
        vec2 uv = ground / tex_scale;
        // a second, coarser scale breaks the tiling as the ground recedes
        float near = 1.0 - smoothstep(60.0, 900.0, length(VERTEX));
        vec2 uv_far = ground / (tex_scale * 7.3);
        vec3 grass = mix(style_tinted(tex_grass, uv_far, col, tex_color_weight),
                         style_tinted(tex_grass, uv, col, tex_color_weight), near);
        vec3 forest = style_tinted(tex_forest, uv, col, tex_color_weight * 0.5);
        vec3 grey = style_tinted(tex_gravel, uv, col, 0.1) * 0.85;
        vec3 snow = style_tinted(tex_snow, uv, col, 0.6);
        col = grass * w_grass + forest * w_forest + grey * w_grey + snow * w_snow;
        // up close, the ground's relief from its normal maps
        if (near > 0.0) {
            vec3 nn = style_ground_normal(n, nrm_grass, uv, 0.6) * w_grass
                    + style_ground_normal(n, nrm_forest, uv, 0.6) * w_forest
                    + style_ground_normal(n, nrm_gravel, uv, 0.6) * (w_grey + w_snow);
            n = normalize(mix(n, nn, near * (1.0 - rockiness)));
        }
        // cliffs: rock, triplanar so it does not smear down the face
        if (rockiness > 0.01) {
            vec3 bw = pow(abs(n), vec3(4.0));
            bw /= bw.x + bw.y + bw.z;
            float rs = tex_scale * 2.0;
            vec3 rock = texture(tex_rock, vec2(ground.y, world_pos.y) / rs).rgb * bw.x
                      + texture(tex_rock, ground / rs).rgb * bw.y
                      + texture(tex_rock, vec2(ground.x, world_pos.y) / rs).rgb * bw.z;
            col = mix(col, rock * 0.9, rockiness);
        }
    }
#else
    col = mix(col, vec3(0.30, 0.28, 0.26), rockiness * 0.75);
#endif

#ifdef STYLE_LIT
    // valley mist of a running occasion; a no-op on an ordinary day
    col = occasion_mist(col, (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz, length(VERTEX));
    col = style_grade(col);
#ifdef STYLE_CARTOON
    // big painted patches of lighter and darker ground
    vec2 paint_xz = pattern_xz(world_pos);
    col *= mix(0.9, 1.1, style_paint_noise(paint_xz / 38.0) * 0.6 + style_paint_noise(paint_xz / 9.0) * 0.4);
#endif
    ALBEDO = col;
    NORMAL = style_view_normal(VIEW_MATRIX, n);
#ifdef STYLE_REAL
    ROUGHNESS = 0.92;
#else
    ROUGHNESS = 1.0;
#endif
    EMISSION = col * door_light(world_pos, n);
#else
    float ndl = clamp(dot(n, normalize(world_sun_dir)), 0.0, 1.0);
    // cliffs get a harder light ramp so faceting reads as rock rather than soft ground
    float levels = mix(light_levels, 3.0, rockiness);
    float light = retro_steps(ndl, levels) * mix(0.55, 0.75, rockiness) + mix(0.45, 0.25, rockiness);
    col *= light;

    col *= world_tint + door_light(world_pos, n);
    // valley mist of a running occasion; a no-op on an ordinary day
    col = occasion_mist(col, (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz, length(VERTEX));
    col = retro_posterize(col, FRAGCOORD.xy);

    float fog = smoothstep(fog_start, fog_end, length(VERTEX));
    ALBEDO = mix(col, world_sky, fog);
#endif
}

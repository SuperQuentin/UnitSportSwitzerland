// Water body, shared by every style's water wrapper (ps1_water, cartoon_water, real_water). Each
// wrapper is shader_type, render_mode (with depth_draw_always: the surface is drawn in the
// transparent pass, unsorted within a tile and sorted only roughly between tiles, so without its
// own depth a farther tile or crest painted over a nearer one in polygons), its defines, then
// this include.
//
// Every style moves the surface with the same physical waves (#299, common/waves, in vertex()):
// what is drawn is what a hull rides on. The fine ripples (water_detail) only shade.
//   PS1      banded blue with a dithered shimmer, semitransparent and clearer toward the shore,
//            a bright banded underside from below
//   Cartoon  cel-lit flat colour bands by depth, crisp white foam on the shore and the crests,
//            see-through in the shallows
//   Real     Fresnel, sky and sun reflection (Godot's PBR), refraction of the bed through the
//            water with depth absorption, detail ripples; screen-space reflections when the
//            material's `ssr_steps` > 0 (Realistic+ on Forward+, set by Styles/StyleKit)

#include "res://shaders/common/world.gdshaderinc"
#include "res://shaders/common/retro.gdshaderinc"
#include "res://shaders/common/style.gdshaderinc"
#include "res://shaders/world_occasion.gdshaderinc"
#include "res://shaders/portal_clip.gdshaderinc"
#include "res://shaders/world_origin.gdshaderinc"
#include "res://shaders/common/waves.gdshaderinc"

uniform float fog_start = 3500.0;
uniform float fog_end = 8000.0;
uniform vec3 fog_color : source_color = vec3(0.72, 0.78, 0.86);
uniform vec3 deep_color : source_color = vec3(0.13, 0.24, 0.36);
uniform vec3 shallow_color : source_color = vec3(0.25, 0.43, 0.52);
uniform vec3 glint_color : source_color = vec3(0.62, 0.76, 0.82);
uniform float wave_speed = 0.35;
// the scene behind the water (opaque only: the water itself is not in it)
uniform sampler2D water_depth_texture : hint_depth_texture, filter_nearest;
#ifndef STYLE_LIT
// semitransparency: alpha at the waterline and through `fade_depth` metres of water and more
uniform float shore_alpha = 0.45;
// the shallows' own tint, so a sandy shelf reads as water, not as wet ground
uniform vec3 shallows_color : source_color = vec3(0.20, 0.58, 0.64);
uniform vec3 shore_line_color : source_color = vec3(0.78, 0.88, 0.88);
uniform float deep_alpha = 0.92;
uniform float fade_depth = 6.0;
uniform float below_alpha = 0.8;
uniform vec3 underside_color : source_color = vec3(0.55, 0.72, 0.78);
#endif
#ifdef STYLE_CARTOON
// flat bands by the depth of water seen through: shallow, mid, deep, abyss
uniform vec3 toon_shallow : source_color = vec3(0.36, 0.78, 0.80);
uniform vec3 toon_mid : source_color = vec3(0.17, 0.58, 0.72);
uniform vec3 toon_deep : source_color = vec3(0.10, 0.38, 0.62);
uniform vec3 toon_abyss : source_color = vec3(0.07, 0.25, 0.48);
uniform vec3 toon_foam : source_color = vec3(0.97, 0.98, 1.0);
uniform vec3 toon_underside : source_color = vec3(0.78, 0.96, 0.96);
uniform float foam_shore_m = 0.035;
#endif
#ifdef STYLE_REAL
uniform sampler2D water_screen_texture : hint_screen_texture, filter_linear_mipmap;
// what a metre of water takes out of red, green and blue: clear at the shore, blue-green deep
uniform vec3 absorption = vec3(0.45, 0.16, 0.12);
// the colour the water scatters back (lit by the sun and the sky)
uniform vec3 scatter_color : source_color = vec3(0.015, 0.075, 0.085);
uniform float refraction = 0.035;
uniform float ripple_strength = 0.4;
// screen-space reflection steps: 0 on Realistic−, Realistic+ on Forward+ sets it (StyleKit)
uniform int ssr_steps = 0;
#endif

varying vec3 world_pos;
// the physical waves at this point: the surface normal, the lift above the still level and the
// water layer's wave scale there (#299)
varying vec3 wave_normal;
varying float wave_lift;
varying float wave_scale;

void vertex() {
    // UV.x: the water layer's wave scale (fetch x depth). The tile's transform is a translation,
    // so the world displacement is the local one. The short waves fade out with distance (a
    // 2 m-spaced vertex's worth at 500 m, 4 m at 1 km), never by the mesh's own spacing: two tiles
    // meshed 2 m and 4 m apart share their edge vertices, and must move them alike (UV.y, the
    // spacing, would crack the seam)
    vec3 rest = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    float spacing = max(2.0, distance(rest, CAMERA_POSITION_WORLD) * 0.008);
    vec3 n;
    vec3 d = water_wave_displace(pattern_xz(rest), UV.x, spacing, n);
    wave_normal = n;
    VERTEX += d;
    wave_lift = d.y;
    wave_scale = UV.x;
    world_pos = rest + d;
    POSITION = retro_snap(PROJECTION_MATRIX * MODELVIEW_MATRIX * vec4(VERTEX, 1.0));
}

// ---- fine ripples: shading only ----------------------------------------------------------

// hash without sine (Dave Hoskins): 0..1, steady for the cell numbers used here
float wd_hash(vec2 p) {
    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

// Smooth value noise, `cells` cells per 9.6 km of ground, wrapped there (so the floating origin
// cannot move it: an axis-aligned lattice wrapped every 9.6 km repeats only every 9.6 km), 0..1.
float wd_noise(vec2 p, float cells) {
    vec2 q = p * (cells / 9600.0);
    vec2 i = floor(q);
    vec2 f = q - i;
    vec2 u = f * f * (3.0 - 2.0 * f);
    float a = wd_hash(mod(i, cells));
    float b = wd_hash(mod(i + vec2(1.0, 0.0), cells));
    float c = wd_hash(mod(i + vec2(0.0, 1.0), cells));
    float d = wd_hash(mod(i + vec2(1.0, 1.0), cells));
    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

// One ripple train, `n` whole cycles over 9.6 km and `m` over the 1200 s wave clock (no jump at
// an origin shift or at the clock's wrap), its phase shifted by `warp`; value and gradient per
// metre (the warp's own slope left out: it is slow).
vec3 wd_train(vec2 p, vec2 n, float m, float warp) {
    vec2 k = n * (6.2831853 / 9600.0);
    float th = dot(k, p) - m * (6.2831853 / 1200.0) * water_time + warp;
    return vec3(sin(th), cos(th) * k);
}

// The fine ripples every style shades with (#299): six trains of 1.35-3.4 m spread round the
// compass at unrelated lengths, their phases warped by five slower trains (6-57 m) and their
// strengths swayed by two of them, so no two line up into a lattice and no patch repeats. Pure sines: the
// noise lattices tried before showed their cells (value noise as creases; a hashed gradient
// noise as hairline breaks along its cell edges on this GPU), and two crossing trains alone (the
// old sin x + sin z shimmer in four bands) drew a regular grid of ellipses on calm water.
// x: about -1..1; yz: its gradient per metre.
vec3 water_detail(vec2 p) {
    float s1 = sin(dot(vec2(196.0, 128.0) * (6.2831853 / 9600.0), p) - 234.0 * (6.2831853 / 1200.0) * water_time);
    float s2 = sin(dot(vec2(-79.0, 149.0) * (6.2831853 / 9600.0), p) - 199.0 * (6.2831853 / 1200.0) * water_time + 1.3);
    // and three of 6-14 m, which scramble the phases within a few ripples: without them the two
    // strongest trains still beat into rows of blobs
    float s3 = sin(dot(vec2(232.0, 1006.0) * (6.2831853 / 9600.0), p) - 246.0 * (6.2831853 / 1200.0) * water_time + 0.4);
    float s4 = sin(dot(vec2(654.0, -251.0) * (6.2831853 / 9600.0), p) - 203.0 * (6.2831853 / 1200.0) * water_time + 2.2);
    float s5 = sin(dot(vec2(-1390.0, 739.0) * (6.2831853 / 9600.0), p) - 304.0 * (6.2831853 / 1200.0) * water_time + 4.0);
    float warp = s1 * 1.7 + s2 * 1.3 + s3 * 2.1 + s4 * 1.8 + s5 * 1.4;
    vec3 sum = wd_train(p, vec2(2762.0, 587.0), 813.0, warp) * (0.55 + 0.25 * s2)
        + wd_train(p, vec2(1724.0, 3110.0), 913.0, 0.7 - warp + s3 * 2.0) * (0.45 + 0.25 * s1)
        + wd_train(p, vec2(-1106.0, 4436.0), 1035.0, 2.9 + warp * 0.6 - s4 * 1.5) * (0.4 - 0.2 * s2 * s5)
        + wd_train(p, vec2(-4736.0, 3076.0), 1150.0, 4.4 - warp * 0.8) * (0.3 - 0.15 * s1)
        + wd_train(p, vec2(-6800.0, -2079.0), 1290.0, 1.1 + warp * 1.2) * 0.2
        + wd_train(p, vec2(-1816.0, -3564.0), 968.0, 5.3 + warp * 0.4) * (0.35 + 0.2 * s1 * s2);
    return sum * 0.55;
}

// The total wave amplitude of the sea state here (the pushed amplitudes times the wave scale).
float wave_height_here() {
    return (water_amp_a.x + water_amp_a.y + water_amp_a.z + water_amp_a.w + water_amp_b.x + water_amp_b.y) * wave_scale;
}

void fragment() {
    if (portal_clipped(world_pos, CAMERA_POSITION_WORLD)) {
        discard;
    }
    vec2 ground = pattern_xz(world_pos);
    float dist = length(VERTEX);
    vec3 det = water_detail(ground);
    // far off, the waves' normal is interpolated over a few pixels' worth of triangles: shading by
    // it draws the mesh's own grid over a whole lake (seen from above Nyon), so it eases to up
    vec3 wave_n = normalize(mix(wave_normal, vec3(0.0, 1.0, 0.0), smoothstep(400.0, 1500.0, dist)));
    // the water between this surface and what is behind it, along the view
    float scene_depth = texture(water_depth_texture, SCREEN_UV).r;
    vec4 behind = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, scene_depth, 1.0);
    float through = max(0.0, -behind.z / behind.w + VERTEX.z);
    // and straight down to it: the depth of the water here (the sky behind: very deep)
    vec3 bed = (INV_VIEW_MATRIX * vec4(behind.xyz / behind.w, 1.0)).xyz;
    float depth = scene_depth <= 0.0 ? 1000.0 : max(0.0, world_pos.y - bed.y);

#ifndef STYLE_LIT
    // PS1 (#299): the bed shows through the shallows, fading to opaque by `fade_depth` metres
    // of water under the surface; from below (FRONT_FACING false under cull_disabled) a bright,
    // nearly opaque skin
    float clear = smoothstep(0.0, fade_depth, depth);
    ALPHA = FRONT_FACING ? mix(shore_alpha, deep_alpha, clear) : below_alpha;
    ALPHA = floor(ALPHA * 8.0 + bayer4(FRAGCOORD.xy)) / 8.0;

    // the shimmer: the fine ripples near, broad painted patches further out (fine ripples would
    // alias into noise there), drifting
    float far = smoothstep(40.0, 320.0, dist);
    float patches = (wd_noise(ground + vec2(water_time * 0.3, water_time * -0.2), 410.0) - 0.5) * 2.4;
    float w = mix(det.x, patches, far);
    // the physical waves take the bands over as the sea gets up: crests light, troughs dark, a
    // slope facing the sun lighter; the fine ripples stay on them as a quarter of the shading
    float amp = wave_height_here();
    float wave_amount = clamp(amp * 1.6, 0.0, 0.8);
    vec3 sun = normalize(world_sun_dir);
    float swell = wave_lift / max(amp, 0.05) * 1.1 + (dot(wave_n, sun) - sun.y) * 6.0;
    w = mix(w, swell + w * 0.25, wave_amount);
    float band = floor((clamp(w, -1.0, 1.0) * 0.5 + 0.5) * 4.0) / 4.0;

    vec3 col = mix(deep_color, shallow_color, band);
    // the shallows: a lighter blue-green, the bands still on it, so a shelf reads as water
    col = mix(col, shallows_color * (0.8 + 0.35 * band), (1.0 - smoothstep(0.3, 3.0, depth)) * 0.85);
    // and a broken pale line where the water meets the dry land
    float edge = (1.0 - smoothstep(0.03, 0.09, depth)) * step(0.35, wd_noise(ground, 2400.0));
    col = mix(col, shore_line_color, edge);
    ALPHA = max(ALPHA, edge * 0.85);
    // from below: a bright skin, the waves' slopes in its bands
    col = FRONT_FACING ? col : underside_color * (0.5 + 0.7 * band);

    // sparse glints on the crests, in cells of a rotated grid that a slow noise switches on and
    // off: kept small and infrequent, or they read as foam rather than light on the surface
    float crest = step(0.86, w * 0.5 + 0.5);
    vec2 cell = floor(mat2(vec2(0.83, 0.56), vec2(-0.56, 0.83)) * ground * 2.3);
    float sparkle = step(0.8, wd_hash(cell + floor(water_time * 3.0) * 7.31))
        * step(0.45, wd_noise(ground, 600.0));
    col = mix(col, glint_color, crest * sparkle * 0.55 * (1.0 - smoothstep(120.0, 400.0, dist)));

    col *= world_tint;
    // valley mist of a running occasion; a no-op on an ordinary day
    col = occasion_mist(col, (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz, dist);
    col = retro_posterize(col, FRAGCOORD.xy);

    float fog = smoothstep(fog_start, fog_end, dist);
    ALBEDO = mix(col, world_sky, fog);
#else
    // the lit styles: the physical normal, tilted by the fine ripples up close, gentler on a
    // calm day (the sea state's summed amplitude, before the local scale: 0.04 calm .. 1.2 gamey)
    float near = 1.0 - smoothstep(30.0, 220.0, dist);
    float sea = clamp((water_amp_a.x + water_amp_a.y + water_amp_a.z + water_amp_a.w) / 1.1, 0.0, 1.0);
#ifdef STYLE_REAL
    float ripple = ripple_strength;
#else
    float ripple = 0.25;
#endif
    vec3 n_world = normalize(wave_n - vec3(det.y, 0.0, det.z) * ripple * near * (0.45 + 0.8 * sea));
    // from below, the surface faces down
    vec3 n_face = FRONT_FACING ? n_world : -n_world;
    NORMAL = style_view_normal(VIEW_MATRIX, n_face);
    vec3 mist_at = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;

#ifdef STYLE_CARTOON
    // flat bands by the depth seen through, their edges wobbling with the ripples so they are
    // not contour lines of the bed
    float d = depth * (0.85 + 0.3 * wd_noise(ground, 700.0)) + det.x * 0.12;
    vec3 col = d < 0.9 ? toon_shallow : d < 3.0 ? toon_mid : d < 9.0 ? toon_deep : toon_abyss;
    // the ripples as a second, lighter tone of the band, up close
    col = mix(col, col * 1.15 + 0.035, step(0.62, det.x * 0.5 + 0.5) * near);
    // foam: a crisp line where the water runs out on the shore (the last few centimetres of
    // depth), and flecks on the crests of waves big enough to break (relative to the sea state
    // here), broken up by the ripples
    float shore = (1.0 - step(foam_shore_m * (0.6 + 0.8 * (det.x * 0.5 + 0.5)), depth))
        * step(0.3, wd_noise(ground + vec2(water_time * 0.4, 0.0), 2400.0));
    float amp = wave_height_here();
    float crest = step(0.15, amp) * step(0.8 * amp, wave_lift + det.x * 0.18 * amp);
    float foam = max(shore, crest) * (FRONT_FACING ? 1.0 : 0.0);
    col = FRONT_FACING ? col : toon_underside;
    ALBEDO = style_grade(occasion_mist(mix(col, toon_foam, foam), mist_at, dist));
    ROUGHNESS = 0.6;
    ALPHA = FRONT_FACING ? max(mix(0.35, 0.97, smoothstep(0.0, 4.0, depth)), foam) : 0.88;
#endif

#ifdef STYLE_REAL
    float cos_v = clamp(dot(NORMAL, VIEW), 0.0, 1.0);
    // Schlick, water's F0 0.02: what the surface reflects; Godot's PBR adds the sky and the sun
    // from SPECULAR and ROUGHNESS, so only the transmitted part is ours
    float fresnel = 0.02 + 0.98 * pow(1.0 - cos_v, 5.0);

    // refraction: the scene behind, displaced by the slope, less where the water is thin; a sample
    // that lands on something in front of the water is not refracted
    vec2 offset = n_world.xz * refraction * clamp(through, 0.0, 2.0) / max(1.0, dist * 0.05);
    vec2 ruv = clamp(SCREEN_UV + offset, vec2(0.001), vec2(0.999));
    float rdepth = texture(water_depth_texture, ruv).r;
    vec4 rbehind = INV_PROJECTION_MATRIX * vec4(ruv * 2.0 - 1.0, rdepth, 1.0);
    float rthrough = -rbehind.z / rbehind.w + VERTEX.z;
    if (rthrough < 0.0) {
        ruv = SCREEN_UV;
        rthrough = through;
    }
    vec3 seen = textureLod(water_screen_texture, ruv, 0.0).rgb;
    // the light through the water column, coloured by what it absorbed (a far sky behind the
    // water seen from above is the bed of a very deep lake: absorbed to nothing)
    vec3 trans = exp(-absorption * min(rthrough, 200.0));
    float specular = 0.5;
    vec3 reflected = vec3(0.0);
    if (ssr_steps > 0 && FRONT_FACING) {
        // screen-space reflection (Realistic+): march the reflected ray against the opaque depth
        vec3 r = reflect(normalize(VERTEX), NORMAL);
        float t = 1.0;
        for (int i = 0; i < ssr_steps; i++) {
            vec3 p = VERTEX + r * t;
            vec4 c = PROJECTION_MATRIX * vec4(p, 1.0);
            if (c.w <= 0.0) {
                break;
            }
            vec2 uv = c.xy / c.w * 0.5 + 0.5;
            if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) {
                break;
            }
            float sd = texture(water_depth_texture, uv).r;
            vec4 sv = INV_PROJECTION_MATRIX * vec4(uv * 2.0 - 1.0, sd, 1.0);
            float scene_z = sv.z / sv.w;
            if (sd > 0.0 && p.z < scene_z && p.z > scene_z - max(1.0, t * 0.25)) {
                float edge = min(min(uv.x, 1.0 - uv.x), min(uv.y, 1.0 - uv.y));
                float hit = smoothstep(0.0, 0.08, edge);
                reflected = textureLod(water_screen_texture, uv, 0.0).rgb * hit;
                specular = 0.5 * (1.0 - hit);
                break;
            }
            t *= 1.35;
        }
    }
    if (FRONT_FACING) {
        EMISSION = seen * trans * (1.0 - fresnel) + reflected * fresnel;
        // a thin broken wet line where the water meets the dry land
        float edge = (1.0 - smoothstep(0.02, 0.06, depth)) * step(0.4, wd_noise(ground, 2400.0));
        ALBEDO = occasion_mist(mix(scatter_color * (1.0 - trans.g), vec3(0.75, 0.8, 0.8), edge * 0.6), mist_at, dist);
        ROUGHNESS = 0.02 + 0.1 * (1.0 - near);
        SPECULAR = specular;
    } else {
        // from below: the sky through Snell's window, the water mirrored outside it
        float window = smoothstep(0.55, 0.75, cos_v);
        EMISSION = mix(vec3(0.07, 0.34, 0.38), seen * vec3(0.7, 0.88, 0.92), window);
        ALBEDO = vec3(0.0);
        ROUGHNESS = 0.2;
        SPECULAR = 0.2;
    }
    ALPHA = 1.0;
#endif
#endif
}

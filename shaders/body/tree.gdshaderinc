// Tree body, shared by ps1_tree (the 3D trees, near) and ps1_treefar (billboards, far:
// TREE_BILLBOARD). Each wrapper is shader_type, render_mode, its defines, then this include.

#include "res://shaders/common/world.gdshaderinc"
#include "res://shaders/common/retro.gdshaderinc"
#include "res://shaders/common/sightline.gdshaderinc"
#include "res://shaders/world_occasion.gdshaderinc"
#include "res://shaders/door_light.gdshaderinc"
#include "res://shaders/portal_clip.gdshaderinc"

// Trees are MultiMesh instances sharing one cone; colour comes from the per-instance
// colour, with the same snap/dither/fog treatment as everything else.

uniform float fog_start = 3500.0;
uniform float fog_end = 8000.0;
uniform vec3 fog_color : source_color = vec3(0.72, 0.78, 0.86);

varying vec3 world_pos;
varying vec3 view_pos;

// --- near-camera fade -------------------------------------------------------------------
// Trees close to the lens AND in front of it dissolve, for every camera (on foot, chase cam,
// free fly, replay). Walking a forest trail at eye height otherwise puts a canopy across the
// whole frame. The periphery is kept solid so a forest still reads as one around you.
// View-space and uniform-driven only: nothing on the C# side changes per frame.
uniform float near_fade = 1.0;       // 0 disables
uniform float near_fade_in = 1.5;    // fully dissolved closer than this (m)
uniform float near_fade_out = 8.0;   // fully solid beyond this (m)
uniform float near_fade_cone = 0.55; // cos of the off-axis angle where it starts to apply

float near_keep(vec3 v) {
    if (near_fade <= 0.0) {
        return 1.0;
    }
    float d = length(v);
    if (d >= near_fade_out || d < 1e-3) {
        return 1.0;
    }
    float close = 1.0 - smoothstep(near_fade_in, near_fade_out, d);
    float ahead = smoothstep(near_fade_cone, 0.95, -v.z / d);
    return 1.0 - near_fade * close * ahead;
}

// --- tree LOD (Styles/StyleKit) -----------------------------------------------------------
// The 3D tree inside tree_near of the camera, a billboard beyond, with a dithered crossfade
// between. Measured from world_cam_pos (the player's camera, Styles/CameraGlobal), not
// CAMERA_POSITION_WORLD, so every view of the world agrees on which trees are 3D.
//
// The billboard is not a picture of a tree but a quad that ray-traces one: each pixel intersects
// its view ray with the unit mesh of Terrain/ChunkNode (ConeMesh, CrownMesh). Both crowns are
// convex 10-face bipyramids and the trunk a 5-sided prism, so that is a handful of plane tests,
// and the silhouette and the flat facets match the 3D tree from any angle: a camera-facing
// sprite would shrink to a sliver seen from above, and the forest floor would show through.
global uniform vec3 world_cam_pos;
uniform bool tree_lod = false;   // the 3D trees: collapse beyond tree_near (the tile has billboards)
uniform float tree_near = 220.0;
uniform float tree_fade = 40.0;
varying float lod_keep;
#ifdef TREE_BILLBOARD
varying vec3 bb_origin;    // the instance's base, world space
varying vec3 bb_scale;     // (radius, height, radius): the instance basis is diagonal
varying float bb_kind;     // 0 conifer, 1 broadleaf
#endif

void vertex() {
#ifdef TREE_BILLBOARD
    // world_vertex_coords: rebuild the quad around the tree, square to the rendering camera and
    // just covering the tree's outline seen from there
    vec3 origin = MODEL_MATRIX[3].xyz;
    float r = length(MODEL_MATRIX[0].xyz);
    float h = length(MODEL_MATRIX[1].xyz);
    lod_keep = smoothstep(tree_near, tree_near + tree_fade, distance(origin, world_cam_pos));
    vec3 centre = origin + vec3(0.0, h * 0.5, 0.0);
    vec3 fwd = normalize(centre - CAMERA_POSITION_WORLD);
    vec3 right = cross(fwd, vec3(0.0, 1.0, 0.0));
    right = dot(right, right) > 1e-6 ? normalize(right) : vec3(1.0, 0.0, 0.0);
    vec3 up = cross(right, fwd);
    // a body of revolution of radius r and height h, seen from elevation e, spans 2r across and
    // h cos(e) + 2r sin(e) up; 10% more for the perspective in front of the quad's plane
    float sin_e = abs(fwd.y);
    float cos_e = sqrt(max(1.0 - sin_e * sin_e, 0.0));
    vec2 half_size = vec2(r, h * 0.5 * cos_e + r * sin_e) * 1.1;
    vec3 wp = centre + right * (UV.x * 2.0 - 1.0) * half_size.x + up * (UV.y * 2.0 - 1.0) * half_size.y;
    if (lod_keep <= 0.0) {
        wp = origin;  // collapsed: the 3D tree stands here
    }
    bb_origin = origin;
    bb_scale = vec3(r, h, r);
    bb_kind = UV2.x;
    VERTEX = wp;
    NORMAL = -fwd;
    world_pos = wp;
    view_pos = (VIEW_MATRIX * vec4(wp, 1.0)).xyz;
    POSITION = retro_snap(PROJECTION_MATRIX * VIEW_MATRIX * vec4(wp, 1.0));
#else
    lod_keep = 1.0;
    if (tree_lod) {
        lod_keep = 1.0 - smoothstep(tree_near, tree_near + tree_fade, distance(MODEL_MATRIX[3].xyz, world_cam_pos));
        if (lod_keep <= 0.0) {
            VERTEX = vec3(0.0);  // collapsed: the billboard stands here
        }
    }
    world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    view_pos = (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
    POSITION = retro_snap(PROJECTION_MATRIX * MODELVIEW_MATRIX * vec4(VERTEX, 1.0));
#endif
}

#ifdef TREE_BILLBOARD
// Clips the ray o + t d to the half-space dot(n, p) <= w: [t_in, t_out] narrows, and n_in is the
// plane the ray last entered through.
void clip_plane(vec3 n, float w, vec3 o, vec3 d, inout float t_in, inout float t_out, inout vec3 n_in) {
    float dn = dot(n, d);
    float t = (w - dot(n, o)) / dn;
    if (abs(dn) < 1e-7) {
        if (dot(n, o) > w) {
            t_in = 1e30;  // parallel and outside: a miss
        }
    } else if (dn < 0.0) {
        if (t > t_in) {
            t_in = t;
            n_in = n;
        }
    } else {
        t_out = min(t_out, t);
    }
}

// The ray (unit-mesh space) against a 5-sided bipyramid: rim of radius 1 at height rim, apex at
// 1, bottom point at low. Returns the entry t (1e30 for a miss) and the facet's normal.
float hit_crown(vec3 o, vec3 d, float rim, float low, out vec3 n) {
    const float APOTHEM = 0.80901699;  // cos(pi / 5): the faces' distance from the axis at the rim
    float t_in = -1e30, t_out = 1e30;
    n = vec3(0.0, 1.0, 0.0);
    for (int i = 0; i < 5; i++) {
        float a = TAU * (float(i) + 0.5) / 5.0;
        vec2 radial = vec2(cos(a), sin(a));
        // upper face: through the apex (0, 1, 0) and the rim edge
        vec3 up_n = vec3(radial.x * (1.0 - rim), APOTHEM, radial.y * (1.0 - rim));
        clip_plane(up_n, APOTHEM, o, d, t_in, t_out, n);
        // lower face: through the bottom point (0, low, 0) and the rim edge
        vec3 low_n = vec3(radial.x * (rim - low), -APOTHEM, radial.y * (rim - low));
        clip_plane(low_n, -APOTHEM * low, o, d, t_in, t_out, n);
    }
    return t_in <= t_out ? t_in : 1e30;
}

// The ray against the trunk: a 5-sided prism of apothem 0.1 * cos(pi / 5), from 0 up to top.
float hit_trunk(vec3 o, vec3 d, float top, out vec3 n) {
    float t_in = -1e30, t_out = 1e30;
    n = vec3(0.0, 1.0, 0.0);
    for (int i = 0; i < 5; i++) {
        float a = TAU * (float(i) + 0.5) / 5.0;
        clip_plane(vec3(cos(a), 0.0, sin(a)), 0.1 * 0.80901699, o, d, t_in, t_out, n);
    }
    clip_plane(vec3(0.0, 1.0, 0.0), top, o, d, t_in, t_out, n);
    clip_plane(vec3(0.0, -1.0, 0.0), 0.0, o, d, t_in, t_out, n);
    return t_in <= t_out ? t_in : 1e30;
}
#endif

void fragment() {
#ifdef TREE_BILLBOARD
    // this pixel's view ray, in the unit mesh's space (the instance basis is a diagonal scale)
    vec3 ray = normalize(world_pos - CAMERA_POSITION_WORLD);
    vec3 o = (CAMERA_POSITION_WORLD - bb_origin) / bb_scale;
    vec3 d = ray / bb_scale;
    // ChunkNode's meshes: conifer rim 0.28 over a neck at 0.22, broadleaf waist 0.62 over 0.34
    bool broadleaf = bb_kind > 0.5;
    float neck = broadleaf ? 0.34 : 0.22;
    vec3 crown_n, trunk_n;
    float t_crown = hit_crown(o, d, broadleaf ? 0.62 : 0.28, neck, crown_n);
    float t_trunk = hit_trunk(o, d, neck, trunk_n);
    float t = min(t_crown, t_trunk);
    if (t > 1e29) {
        discard;
    }
    // back to world space: the hit point, and the facet normal through the inverse transpose
    vec3 wpos = CAMERA_POSITION_WORLD + ray * t;
    vec3 n = normalize((t_crown < t_trunk ? crown_n : trunk_n) / bb_scale);
#else
    vec3 wpos = world_pos;
    vec3 n = normalize(cross(dFdy(world_pos), dFdx(world_pos)));
#endif
    if (portal_clipped(wpos, CAMERA_POSITION_WORLD)) {
        discard;
    }
    float ndl = clamp(dot(n, normalize(world_sun_dir)), 0.0, 1.0);
    float light = retro_steps(ndl, 3.0) * 0.45 + 0.6;
    // snow settles on the upper faces of a crown (a running occasion's world_snow)
    vec3 col = occasion_snow_amount(COLOR.rgb, smoothstep(0.25, 0.75, abs(n.y)) * 0.8) * light;

    // dissolve anything between the camera and its subject, or right in front of the lens, and
    // crossfade 3D and billboard, using the same 4x4 Bayer pattern the colour quantiser below
    // uses, so the fade reads as part of the renderer
    float threshold = bayer4(FRAGCOORD.xy);
#ifdef TREE_BILLBOARD
    // the billboard takes exactly the pixels the 3D tree gives up (it keeps those where
    // 1 - lod_keep >= threshold), or mid-fade a pixel would show neither and the ground between
    if (lod_keep < 1.0 && lod_keep <= 1.0 - threshold) {
        discard;
    }
    float keep = min(sightline_keep(wpos), near_keep(view_pos));
#else
    float keep = min(min(sightline_keep(wpos), near_keep(view_pos)), lod_keep);
#endif
    if (keep < threshold) {
        discard;
    }

    col *= world_tint + door_light(wpos, n);
    // valley mist of a running occasion; a no-op on an ordinary day
    col = occasion_mist(col, (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz, length(VERTEX));
    col = retro_posterize(col, FRAGCOORD.xy);

    float fog = smoothstep(fog_start, fog_end, length(VERTEX));
    ALBEDO = mix(col, world_sky, fog);
}

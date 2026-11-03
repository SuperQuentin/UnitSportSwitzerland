// Tree body, shared by ps1_tree (the 3D trees, near) and ps1_treefar (billboards, far:
// TREE_BILLBOARD), and their cartoon_ wrappers. Each wrapper is shader_type, render_mode, its
// defines, then this include. Cartoon's trees are ChunkNode's High-detail meshes, so its
// billboards trace those shapes instead of PS1's.

#include "res://shaders/common/world.gdshaderinc"
#include "res://shaders/common/retro.gdshaderinc"
#include "res://shaders/common/style.gdshaderinc"
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
// CAMERA_POSITION_WORLD, so every view of the world agrees on which trees are 3D. The distance
// is divided by world_lod_scale, how much the camera zooms over the walking view (Styles/LodScale,
// #1433): through a 200 mm lens a tree 2 km off is as big on screen as one 180 m off walking.
//
// The billboard is not a picture of a tree but a quad that ray-traces one: each pixel intersects
// its view ray with the unit mesh of Terrain/ChunkNode (ConeMesh, CrownMesh). Both crowns are
// convex 10-face bipyramids and the trunk a 5-sided prism, so that is a handful of plane tests,
// and the silhouette and the flat facets match the 3D tree from any angle: a camera-facing
// sprite would shrink to a sliver seen from above, and the forest floor would show through.
global uniform vec3 world_cam_pos;
global uniform float world_lod_scale;
#include "res://shaders/common/music.gdshaderinc"
uniform bool tree_lod = false;   // the 3D trees: collapse beyond tree_near (the tile has billboards)
uniform float tree_near = 220.0;
uniform float tree_fade = 40.0;
varying float lod_keep;
#ifdef STYLE_REAL
// The realistic trees (Styles/ModelCatalog: EZ-Tree meshes, two surfaces). Near: the bark or the
// leaf cards; far: the impostors baked from the same meshes (--bake-impostors).
uniform sampler2D albedo_tex : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D normal_tex : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool leaves = false;
uniform bool use_normal_map = false;
uniform vec2 uv_scale = vec2(1.0);
uniform float alpha_cut = 0.45;
// the instance colours (ChunkNode.Pack) vary around these, a broadleaf's and a conifer's: their
// ratio varies each tree's leaves (StyleKit.TreeSurface picks the near trees' one)
uniform vec3 tint_ref : source_color = vec3(0.20, 0.32, 0.15);
uniform vec3 tint_ref_conifer : source_color = vec3(0.165, 0.285, 0.145);
uniform sampler2D impostor_side_conifer : source_color, filter_linear_mipmap, repeat_disable;
uniform sampler2D impostor_top_conifer : source_color, filter_linear_mipmap, repeat_disable;
uniform sampler2D impostor_side_broadleaf : source_color, filter_linear_mipmap, repeat_disable;
uniform sampler2D impostor_top_broadleaf : source_color, filter_linear_mipmap, repeat_disable;
#endif
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
    lod_keep = smoothstep(tree_near, tree_near + tree_fade, distance(origin, world_cam_pos) / world_lod_scale);
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
        lod_keep = 1.0 - smoothstep(tree_near, tree_near + tree_fade, distance(MODEL_MATRIX[3].xyz, world_cam_pos) / world_lod_scale);
        if (lod_keep <= 0.0) {
            VERTEX = vec3(0.0);  // collapsed: the billboard stands here
        }
    }
    world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
#ifdef STYLE_CARTOON
    // Godot turns a MultiMesh instance's normals by its basis, not the inverse transpose: a
    // conifer's (0.26 h, h, 0.26 h) tipped the smooth normals nearly straight up, every side lit
    // like flat ground and pale under snow beside the billboards' traced ones (#1437). Divided by
    // the squared scale here, the basis leaves them divided by the scale, as a normal should be.
    NORMAL /= vec3(dot(MODEL_MATRIX[0].xyz, MODEL_MATRIX[0].xyz), dot(MODEL_MATRIX[1].xyz, MODEL_MATRIX[1].xyz),
        dot(MODEL_MATRIX[2].xyz, MODEL_MATRIX[2].xyz));
#endif
    // near music (#734): the crown sways a swing per two beats, louder wider, and leans away on
    // the kicks; the trunk's foot stays put (the sway grows with the height up the tree)
    vec3 base = MODEL_MATRIX[3].xyz;
    float mr = music_reach(base);
    if (mr > 0.0) {
        float tall = max(length(MODEL_MATRIX[1].xyz), 0.5);
        float up = clamp((world_pos.y - base.y) / tall, 0.0, 1.0);
        vec3 away = normalize(vec3(base.x - world_music.x, 0.0, base.z - world_music.z) + vec3(1e-4, 0.0, 0.0));
        vec3 side = vec3(-away.z, 0.0, away.x);
        float swing = sin(6.2831853 * world_music_beat.w) * (0.3 + 0.7 * world_music_beat.y);
        world_pos += (side * swing * 0.05 + away * world_music_beat.x * 0.035) * tall * mr * up * up;
    }
    view_pos = (VIEW_MATRIX * vec4(world_pos, 1.0)).xyz;
    POSITION = retro_snap(PROJECTION_MATRIX * VIEW_MATRIX * vec4(world_pos, 1.0));
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

#ifdef STYLE_CARTOON
// Cartoon's unit meshes (Terrain/ChunkNode, MeshDetail.High). Change those and these together.

// One 7-sided tier of TieredConeMesh: rim of radius r at height base (corners at angles TAU i / 7),
// apex at top. Its underside is a shallow dent in the mesh, traced flat here.
float hit_tier(vec3 o, vec3 d, float base, float top, float r, out vec3 n) {
    float apothem = r * cos(PI / 7.0);
    float t_in = -1e30, t_out = 1e30;
    n = vec3(0.0, -1.0, 0.0);
    for (int i = 0; i < 7; i++) {
        float a = TAU * (float(i) + 0.5) / 7.0;
        clip_plane(vec3(cos(a) * (top - base), apothem, sin(a) * (top - base)), apothem * top,
            o, d, t_in, t_out, n);
    }
    clip_plane(vec3(0.0, -1.0, 0.0), -base, o, d, t_in, t_out, n);
    return t_in <= t_out ? t_in : 1e30;
}

// The mesh's smooth normal at p on a tier's side face (facet normal n, from hit_tier), unit-mesh
// space: what the GPU interpolates between the face's corners, (cos a, slope, sin a) at each rim
// corner and at the face's middle angle on the apex. The barycentric weights come straight from
// p: the apex's is its height up the tier, and across the face p.xz runs along the rim chord.
vec3 tier_smooth(vec3 p, vec3 n, float base, float top, float r) {
    vec2 mid = normalize(n.xz);
    vec2 across = vec2(-mid.y, mid.x);
    float w = clamp((p.y - base) / (top - base), 0.0, 1.0);
    vec2 xz = (w + (1.0 - w) * cos(PI / 7.0)) * mid + dot(p.xz, across) / r * across;
    return vec3(xz.x, r / (top - base), xz.y);
}

// One puff of PuffCrownMesh, an ellipsoid (the mesh's 6x4 one, traced smooth).
float hit_puff(vec3 o, vec3 d, vec3 c, vec3 radii, out vec3 n) {
    vec3 oc = (o - c) / radii;
    vec3 dd = d / radii;
    float a = dot(dd, dd);
    float b = dot(oc, dd);
    float disc = b * b - a * (dot(oc, oc) - 1.0);
    n = vec3(0.0, 1.0, 0.0);
    if (disc < 0.0) {
        return 1e30;
    }
    float t = (-b - sqrt(disc)) / a;
    n = (oc + dd * t) / radii;
    return t;
}

// Corner (i around, j down) of the puff mesh's 6x4 unit sphere (ChunkNode.Puff).
vec3 puff_corner(float i, float j) {
    float th = PI * j / 4.0, ph = TAU * i / 6.0;
    return vec3(sin(th) * cos(ph), cos(th), sin(th) * sin(ph));
}

// The mesh's facet under u, a point of a puff's unit sphere (normal in that sphere's space). The
// 3D tree's snow settles by its facets (facet_normal), and the smooth ellipsoid spread it over
// the puffs' sides. Each quad of the mesh is two triangles split along b-c; the polar rings keep
// one.
vec3 puff_facet(vec3 u) {
    float th = acos(clamp(u.y, -1.0, 1.0)) / (PI / 4.0);
    float ph = mod(atan(u.z, u.x), TAU) / (TAU / 6.0);
    float j = min(floor(th), 3.0), i = floor(ph);
    vec3 a = puff_corner(i, j), b = puff_corner(i + 1.0, j);
    vec3 c = puff_corner(i, j + 1.0), d = puff_corner(i + 1.0, j + 1.0);
    vec3 n = j < 0.5 || (j < 2.5 && (ph - i) + (th - j) >= 1.0) ? cross(d - b, c - b) : cross(b - a, c - a);
    return dot(n, u) < 0.0 ? -n : n;
}

// The 6-sided trunk prism of both, corners at radius r, from 0 up to top.
float hit_trunk6(vec3 o, vec3 d, float r, float top, out vec3 n) {
    float t_in = -1e30, t_out = 1e30;
    n = vec3(0.0, 1.0, 0.0);
    for (int i = 0; i < 6; i++) {
        float a = TAU * (float(i) + 0.5) / 6.0;
        clip_plane(vec3(cos(a), 0.0, sin(a)), r * cos(PI / 6.0), o, d, t_in, t_out, n);
    }
    clip_plane(vec3(0.0, 1.0, 0.0), top, o, d, t_in, t_out, n);
    clip_plane(vec3(0.0, -1.0, 0.0), 0.0, o, d, t_in, t_out, n);
    return t_in <= t_out ? t_in : 1e30;
}
#endif

#ifdef STYLE_REAL
// The impostor at this view ray (unit-mesh space, o + t d). Two baked pictures of the tree: side
// on (x -1..1, y 0..1), on the vertical plane through the trunk that faces the camera, and from
// above (x, z -1..1), on the horizontal plane at mid crown. Trees are near enough to bodies of
// revolution that one side view serves every azimuth. The camera's elevation picks between
// them. Returns the colour and coverage, the hit's t and a rounded-crown normal.
vec4 impostor(vec3 o, vec3 d, bool broadleaf, float sin_e, out float t, out vec3 n) {
    vec2 toward = length(o.xz) > 1e-4 ? normalize(o.xz) : vec2(0.0, 1.0);
    vec2 across = vec2(-toward.y, toward.x);
    float ts = -dot(o.xz, toward) / dot(d.xz, toward);
    vec3 ps = o + d * ts;
    vec2 side_uv = vec2(dot(ps.xz, across) * 0.5 + 0.5, 1.0 - ps.y);
    vec4 side = vec4(0.0);
    if (ts > 0.0 && side_uv == clamp(side_uv, vec2(0.0), vec2(1.0))) {
        side = broadleaf ? texture(impostor_side_broadleaf, side_uv) : texture(impostor_side_conifer, side_uv);
    }
    float tt = abs(d.y) > 1e-5 ? (0.6 - o.y) / d.y : -1.0;
    vec3 pt = o + d * tt;
    vec2 top_uv = pt.xz * 0.5 + 0.5;
    vec4 top = vec4(0.0);
    if (tt > 0.0 && top_uv == clamp(top_uv, vec2(0.0), vec2(1.0))) {
        top = broadleaf ? texture(impostor_top_broadleaf, top_uv) : texture(impostor_top_conifer, top_uv);
    }
    float w = smoothstep(0.55, 0.85, sin_e);
    t = mix(ts, tt, w);
    vec3 side_n = vec3(across.x, 0.0, across.y) * (side_uv.x * 2.0 - 1.0) * 0.7 + vec3(0.0, 0.45, 0.0)
        + vec3(toward.x, 0.0, toward.y);
    n = normalize(mix(side_n, vec3(0.0, 1.0, 0.0), w));
    return mix(side, top, w);
}
#endif
#endif

void fragment() {
#ifdef TREE_BILLBOARD
    // this pixel's view ray, in the unit mesh's space (the instance basis is a diagonal scale)
    vec3 ray = normalize(world_pos - CAMERA_POSITION_WORLD);
    vec3 o = (CAMERA_POSITION_WORLD - bb_origin) / bb_scale;
    vec3 d = ray / bb_scale;
    bool broadleaf = bb_kind > 0.5;
    vec3 crown_n, trunk_n;
#ifdef STYLE_REAL
    float t_imp;
    vec3 imp_n;
    vec4 imp = impostor(o, d, broadleaf, abs(ray.y), t_imp, imp_n);
    if (imp.a < 0.5) {
        discard;
    }
    float t_crown = t_imp, t_trunk = 1e30;
    crown_n = imp_n;
#elif defined(STYLE_CARTOON)
    // three puffs on a 0.5 trunk, or three tiers on a 0.3 one
    float t_crown = 1e30;
    vec3 hit_n;
    vec3 tier = vec3(0.0);  // the nearest tier's (base, top, r)
    vec3 puff = vec3(1.0);  // the nearest puff's radii
    if (broadleaf) {
        const vec3 PUFF_C[3] = vec3[](vec3(0.0, 0.70, 0.0), vec3(0.34, 0.56, 0.18), vec3(-0.30, 0.58, -0.22));
        const vec3 PUFF_R[3] = vec3[](vec3(0.72, 0.28, 0.72), vec3(0.52, 0.22, 0.52), vec3(0.55, 0.23, 0.55));
        for (int i = 0; i < 3; i++) {
            float t_puff = hit_puff(o, d, PUFF_C[i], PUFF_R[i], hit_n);
            if (t_puff < t_crown) {
                t_crown = t_puff;
                crown_n = hit_n;
                puff = PUFF_R[i];
            }
        }
    } else {
        const vec3 TIERS[3] = vec3[](vec3(0.22, 0.62, 1.0), vec3(0.45, 0.82, 0.75), vec3(0.66, 1.0, 0.5));
        for (int i = 0; i < 3; i++) {
            float t_tier = hit_tier(o, d, TIERS[i].x, TIERS[i].y, TIERS[i].z, hit_n);
            if (t_tier < t_crown) {
                t_crown = t_tier;
                crown_n = hit_n;
                tier = TIERS[i];
            }
        }
    }
    float t_trunk = hit_trunk6(o, d, broadleaf ? 0.07 : 0.06, broadleaf ? 0.5 : 0.3, trunk_n);
#else
    // ChunkNode's meshes: conifer rim 0.28 over a neck at 0.22, broadleaf waist 0.62 over 0.34
    float neck = broadleaf ? 0.34 : 0.22;
    float t_crown = hit_crown(o, d, broadleaf ? 0.62 : 0.28, neck, crown_n);
    float t_trunk = hit_trunk(o, d, neck, trunk_n);
#endif
    float t = min(t_crown, t_trunk);
    if (t > 1e29) {
        discard;
    }
    // back to world space: the hit point, and the facet normal through the inverse transpose
    vec3 wpos = CAMERA_POSITION_WORLD + ray * t;
    vec3 n = normalize((t_crown < t_trunk ? crown_n : trunk_n) / bb_scale);
    // as on the 3D tree, the light takes the mesh's smooth normals and the snow its facets (n)
    vec3 light_n = n;
#ifdef STYLE_CARTOON
    if (t_crown < t_trunk && broadleaf) {
        n = normalize(puff_facet(crown_n * puff) / puff / bb_scale);
    } else if (t_crown < t_trunk && crown_n.y > 0.0) {
        light_n = normalize(tier_smooth(o + d * t, crown_n, tier.x, tier.y, tier.z) / bb_scale);
    }
#endif
#else
    vec3 wpos = world_pos;
    vec3 n = facet_normal(VERTEX, INV_VIEW_MATRIX);
#endif
    if (portal_clipped(wpos, CAMERA_POSITION_WORLD)) {
        discard;
    }
    // snow settles on the upper faces of a crown (a running occasion's world_snow)
    vec3 col = occasion_snow_amount(COLOR.rgb, smoothstep(0.25, 0.75, abs(n.y)) * 0.8);
#ifdef STYLE_REAL
#ifdef TREE_BILLBOARD
    vec3 vary = clamp(COLOR.rgb / (bb_kind > 0.5 ? tint_ref : tint_ref_conifer), vec3(0.75), vec3(1.3));
    col = occasion_snow_amount(imp.rgb * vary, smoothstep(0.25, 0.75, abs(n.y)) * 0.8);
#else
    vec3 vary = clamp(COLOR.rgb / tint_ref, vec3(0.75), vec3(1.3));
    vec4 surface = texture(albedo_tex, UV * uv_scale);
    if (leaves && surface.a < alpha_cut) {
        discard;
    }
    col = occasion_snow_amount(leaves ? surface.rgb * vary : surface.rgb, smoothstep(0.25, 0.75, abs(n.y)) * 0.8);
#endif
#endif
#ifndef STYLE_LIT
    float ndl = clamp(dot(n, normalize(world_sun_dir)), 0.0, 1.0);
    col *= retro_steps(ndl, 3.0) * 0.45 + 0.6;
#endif

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

#ifdef STYLE_LIT
    // valley mist of a running occasion; a no-op on an ordinary day
    col = style_grade(occasion_mist(col, (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz, length(VERTEX)));
    ALBEDO = col;
#ifdef TREE_BILLBOARD
    // the traced normal; the 3D trees light by their mesh's own normals
    NORMAL = style_view_normal(VIEW_MATRIX, light_n);
#endif
#ifdef STYLE_REAL
#ifndef TREE_BILLBOARD
    if (use_normal_map && !leaves) {
        NORMAL_MAP = texture(normal_tex, UV * uv_scale).rgb;
    }
#endif
    ROUGHNESS = leaves ? 0.8 : 0.95;
    // sun through the leaves
    BACKLIGHT = leaves ? vec3(0.22, 0.28, 0.10) : vec3(0.0);
#else
    ROUGHNESS = 1.0;
#endif
    EMISSION = col * door_light(wpos, n);
#else
    col *= world_tint + door_light(wpos, n);
    // valley mist of a running occasion; a no-op on an ordinary day
    col = occasion_mist(col, (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz, length(VERTEX));
    col = retro_posterize(col, FRAGCOORD.xy);

    float fog = smoothstep(fog_start, fog_end, length(VERTEX));
    ALBEDO = mix(col, world_sky, fog);
#endif
}

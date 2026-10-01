// tree body: see shaders/body/style.gdshaderinc for the STYLE_* switches.

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

// Trees are MultiMesh instances sharing one cone; colour comes from the per-instance
// colour, with the same snap/dither/fog treatment as everything else.

uniform vec2 snap_resolution = vec2(640.0, 480.0);
uniform float color_levels = 16.0;
uniform float fog_start = 3500.0;
uniform float fog_end = 8000.0;
uniform vec3 fog_color : source_color = vec3(0.72, 0.78, 0.86);

varying vec3 world_pos;
varying vec3 view_pos;
// lit styles: the High-detail tree meshes carry smooth normals and mark the trunk with UV.x = 1
varying vec3 mesh_normal;
varying float trunk;
uniform vec3 bark_color : source_color = vec3(0.36, 0.26, 0.18);

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


// --- lit styles: per-tree LOD (prototype #181) ---------------------------------------------
// The 3D High-detail tree inside tree_near of the camera, a camera-facing billboard beyond, with
// a dithered crossfade between. Measured from world_cam_pos (the player's camera, set each frame
// by VisualStyleKit), not CAMERA_POSITION_WORLD, so the sun's shadow pass agrees with the screen.
global uniform vec3 world_cam_pos;
uniform bool tree_lod = false;   // set where the tile also has billboards (VisualStyleKit)
uniform float tree_near = 220.0;
uniform float tree_fade = 40.0;
varying float lod_keep;
varying vec2 bb_uv;
varying float bb_kind;     // 0 conifer, 1 broadleaf
#ifdef STYLE_REAL
uniform sampler2D impostor_conifer : source_color, filter_linear_mipmap, repeat_disable;
uniform sampler2D impostor_broadleaf : source_color, filter_linear_mipmap, repeat_disable;
uniform vec3 impostor_tint_ref : source_color = vec3(0.20, 0.32, 0.15);
#endif
varying vec3 bb_right;
varying vec3 bb_fwd;

void vertex() {
#ifdef TREE_BILLBOARD
    // world_vertex_coords: rebuild the quad around the instance origin, facing the camera
    vec3 origin = MODEL_MATRIX[3].xyz;
    float sx = length(MODEL_MATRIX[0].xyz);
    float sy = length(MODEL_MATRIX[1].xyz);
    float d = distance(origin, world_cam_pos);
    lod_keep = smoothstep(tree_near, tree_near + tree_fade, d);
    vec3 to_cam = world_cam_pos - origin;
    to_cam.y = 0.0;
    to_cam = normalize(to_cam + vec3(1e-4, 0.0, 0.0));
    vec3 right = vec3(to_cam.z, 0.0, -to_cam.x);
    vec3 wp = origin + right * (UV.x * 2.0 - 1.0) * sx + vec3(0.0, UV.y * sy, 0.0);
    if (lod_keep <= 0.0) {
        wp = origin;  // collapsed: the 3D tree stands here
    }
    bb_uv = UV;
    bb_kind = UV2.x;
    bb_right = right;
    bb_fwd = to_cam;
    VERTEX = wp;
    NORMAL = to_cam;
#ifdef STYLE_PS1
    POSITION = style_clip(PROJECTION_MATRIX * VIEW_MATRIX * vec4(wp, 1.0), snap_resolution);
#endif
    world_pos = wp;
    view_pos = (VIEW_MATRIX * vec4(wp, 1.0)).xyz;
    mesh_normal = to_cam;
    trunk = 0.0;
#else
    lod_keep = 1.0;
    if (tree_lod) {
        float d = distance(MODEL_MATRIX[3].xyz, world_cam_pos);
        lod_keep = 1.0 - smoothstep(tree_near, tree_near + tree_fade, d);
        if (lod_keep <= 0.0) {
            VERTEX = vec3(0.0);  // collapsed: the billboard stands here
        }
    }
    world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    view_pos = (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
    mesh_normal = (MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz;
    trunk = UV.x;
    vec4 clip = PROJECTION_MATRIX * MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
    POSITION = style_clip(clip, snap_resolution);
#endif
}

#ifdef TREE_BILLBOARD
// The tree's silhouette, drawn on the quad to match the High-detail meshes in ChunkNode
// (PuffCrownMesh / TieredConeMesh). Returns 0 outside; writes a rounded normal and the trunk flag.
float bb_shape(vec2 uv, float kind, vec3 right, vec3 fwd, out vec3 n, out float is_trunk) {
    vec3 up = vec3(0.0, 1.0, 0.0);
    float u = uv.x * 2.0 - 1.0;  // the mesh's own x, -1..1
    float y = uv.y;
    // ragged leafy edge
    float jag = (fract(sin(dot(floor(uv * vec2(24.0, 30.0)), vec2(12.9898, 78.233))) * 43758.5453) - 0.5) * 0.12;
    n = fwd;
    is_trunk = 0.0;
#ifdef STYLE_PS1
    // the PS1 unit meshes (ChunkNode.ConeMesh / CrownMesh): 5-sided, so shade in 5 facets
    float base = kind > 0.5 ? 0.34 : 0.28;
    if (y < (kind > 0.5 ? 0.34 : 0.22)) {
        is_trunk = abs(u) < 0.1 ? 1.0 : 0.0;
        return is_trunk;
    }
    float half_w;
    vec3 tilt;
    if (kind > 0.5) {
        half_w = y < 0.62 ? (y - 0.34) / 0.28 : (1.0 - y) / 0.38;
        tilt = y < 0.62 ? vec3(0.28, -1.0, 0.0) : vec3(0.38, 1.0, 0.0);
    } else {
        half_w = y < base ? (y - 0.22) / 0.06 : (1.0 - y) / 0.72;
        tilt = vec3(0.72, 1.0, 0.0);
    }
    half_w = min(half_w, 1.0);
    if (abs(u) > half_w) {
        return 0.0;
    }
    float a = asin(clamp(u / max(half_w, 1e-3), -1.0, 1.0));
    a = round(a / 1.2566) * 1.2566;
    vec3 dir = right * sin(a) + fwd * cos(a);
    n = normalize(dir * tilt.x + up * tilt.y);
    return 1.0;
#endif
    if (kind > 0.5) {
        vec3 cs[3] = vec3[3](vec3(0.0, 0.70, 0.0), vec3(0.34, 0.56, 0.0), vec3(-0.30, 0.58, 0.0));
        vec2 rs[3] = vec2[3](vec2(0.72, 0.28), vec2(0.52, 0.22), vec2(0.55, 0.23));
        float best = 1e9;
        for (int i = 0; i < 3; i++) {
            vec2 q = (vec2(u, y) - cs[i].xy) / rs[i];
            float d2 = dot(q, q);
            if (d2 < best) {
                best = d2;
                n = normalize(right * q.x + up * q.y * 0.8 + fwd * sqrt(max(1.0 - d2, 0.05)));
            }
        }
        if (best < 1.0 + jag) {
            return 1.0;
        }
        if (abs(u) < 0.08 && y < 0.5) {
            is_trunk = 1.0;
            return 1.0;
        }
        return 0.0;
    }
    vec3 tiers[3] = vec3[3](vec3(0.22, 0.62, 1.0), vec3(0.45, 0.82, 0.75), vec3(0.66, 1.0, 0.5));
    for (int i = 2; i >= 0; i--) {
        vec3 t = tiers[i];
        if (y > t.x && y < t.y) {
            float half_w = t.z * (t.y - y) / (t.y - t.x);
            if (abs(u) < half_w + jag * 0.5) {
                float side = clamp(u / max(half_w, 0.01), -1.0, 1.0);
                n = normalize(right * side * 0.8 + up * (t.z / (t.y - t.x)) * 0.35 + fwd * (1.0 - abs(side) * 0.5));
                return 1.0;
            }
        }
    }
    if (abs(u) < 0.07 && y < 0.3) {
        is_trunk = 1.0;
        return 1.0;
    }
    return 0.0;
}
#endif

void fragment() {
    if (portal_clipped(world_pos, CAMERA_POSITION_WORLD)) {
        discard;
    }
    vec3 n = normalize(cross(dFdy(world_pos), dFdx(world_pos)));
#ifdef TREE_BILLBOARD
    float bb_trunk = 0.0;
#ifdef STYLE_REAL
    // an impostor baked from the 3D EZ-Tree mesh at startup (VisualStyleKit.BakeImpostors)
    vec2 iuv = vec2(bb_uv.x, 1.0 - bb_uv.y);
    vec4 imp = bb_kind > 0.5 ? texture(impostor_broadleaf, iuv) : texture(impostor_conifer, iuv);
    if (imp.a < 0.5) {
        discard;
    }
    n = normalize(bb_right * (bb_uv.x * 2.0 - 1.0) * 0.7 + vec3(0.0, 0.45, 0.0) + bb_fwd);
#else
    if (bb_shape(bb_uv, bb_kind, bb_right, bb_fwd, n, bb_trunk) < 0.5) {
        discard;
    }
#endif
#endif
    // snow settles on the upper faces of a crown (a running occasion's world_snow)
    vec3 col = occasion_snow_amount(COLOR.rgb, smoothstep(0.25, 0.75, abs(n.y)) * 0.8);
#ifdef TREE_BILLBOARD
#ifdef STYLE_REAL
    col = imp.rgb * clamp(COLOR.rgb / impostor_tint_ref, vec3(0.75), vec3(1.3));
#endif
#endif
#ifdef STYLE_PS1
    float ndl = clamp(dot(n, normalize(world_sun_dir)), 0.0, 1.0);
    col *= floor(ndl * 3.0) / 3.0 * 0.45 + 0.6;
#endif

    const float bayer[16] = float[16](
        0.0, 8.0, 2.0, 10.0,
        12.0, 4.0, 14.0, 6.0,
        3.0, 11.0, 1.0, 9.0,
        15.0, 7.0, 13.0, 5.0);

    // dissolve anything between the camera and its subject, or right in front of the lens, using the same 4x4 Bayer pattern
    // the colour quantiser below uses, so the fade reads as part of the renderer
    float keep = min(min(sightline_keep(world_pos), near_keep(view_pos)), lod_keep);
    if (keep < 1.0) {
        int ci = int(mod(FRAGCOORD.x, 4.0)) + 4 * int(mod(FRAGCOORD.y, 4.0));
        if (keep < bayer[ci] / 16.0) {
            discard;
        }
    }

#ifdef STYLE_LIT
#ifdef STYLE_CARTOON
    col = style_cartoon_grade(col) * 1.08;
    ROUGHNESS = 1.0;
#else
    col *= 0.85;
    ROUGHNESS = 0.95;
#endif
    // the High-detail meshes' smooth normals make the crown read as a rounded mass
    vec3 crown = normalize(mesh_normal);
#ifdef TREE_BILLBOARD
    crown = n;
    if (bb_trunk > 0.5) {
        col = bark_color;
    }
#endif
    if (trunk > 0.5) {
        col = bark_color;
    }
    ALBEDO = col;
    NORMAL = style_view_normal(VIEW_MATRIX, crown);
    EMISSION = col * door_light(world_pos, n);
#else
    col *= world_tint + door_light(world_pos, n);
    // valley mist of a running occasion; a no-op on an ordinary day
    col = occasion_mist(col, (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz, length(VERTEX));
    col = style_posterize(col, FRAGCOORD.xy, color_levels);

    float fog = smoothstep(fog_start, fog_end, length(VERTEX));
    ALBEDO = mix(col, world_sky, fog);
#endif
}

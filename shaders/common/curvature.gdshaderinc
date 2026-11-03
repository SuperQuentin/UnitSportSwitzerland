// The Earth's curvature, drawn round the camera (#187, docs/notes/world/earth-curvature.md): the
// world stays flat LV95 on the CPU; a body lowers what it draws by the drop at its flat distance from
// the camera, d² · world_curvature (1 / 2R, Core/Curvature: 246 m at 60 km; 0 = flat, the model viewer).
// Only POSITION curves: VERTEX and world positions stay flat, so normals, patterns, portal clips and
// the horizon cover are as before. Measured from the view's own main camera (MAIN_CAM_INV_VIEW_MATRIX:
// the same in its shadow pass), so a mirror, a portal or a preview viewport drops round its own eye.
// At altitude (#1430, H1): world_curvature follows the camera's height (refraction gone from 20 km, Core/
// Curvature.DropPerM2At), and world_curve_blend (0 below 20 km, 1 from 50 km) turns the drop into the exact
// sphere through the camera's nadir: the flat distance read as arc length, the point drawn in by
// s - (R + y)·sin θ and down by (R + y)·(1 - cos θ), worked camera-relative in floats (never R + y, 1 - cos θ
// as 2·sin²(θ/2)): Core/Curvature.OffsetAsShader is this function in C#, unit-tested against doubles.
#ifndef COMMON_CURVATURE
#define COMMON_CURVATURE
global uniform float world_curvature;
global uniform float world_curve_blend;

// LV95's projection sphere's radius (Core/Curvature.EarthRadiusM)
const float CURVE_EARTH_R = 6378815.904;

// Where a flat world point is drawn, as an offset from it in world axes, seen from `eye`. Its y is its
// altitude (the world's y is the altitude above the sphere).
vec3 curve_offset(vec3 world, vec3 eye) {
    vec2 d = world.xz - eye.xz;
    float s2 = dot(d, d);
    float drop = s2 * world_curvature;
    if (world_curve_blend <= 0.0) return vec3(0.0, -drop, 0.0);
    float s = sqrt(s2);
    if (s < 1e-3) return vec3(0.0, -drop, 0.0);
    vec2 u = d / s;
    float th = s / CURVE_EARTH_R, half_th = th * 0.5;
    float sinc_half = half_th < 1e-3 ? 1.0 - half_th * half_th / 6.0 : sin(half_th) / half_th;
    float sinc_m1 = th < 1e-2 ? -th * th / 6.0 + th * th * th * th / 120.0 : sin(th) / th - 1.0;
    float along = s * sinc_m1 + world.y * sin(th);
    float sin_half = sin(half_th);
    float down = s2 / (2.0 * CURVE_EARTH_R) * sinc_half * sinc_half + world.y * 2.0 * sin_half * sin_half;
    float b = world_curve_blend;
    return vec3(u.x * along * b, -((1.0 - b) * drop + b * down), u.y * along * b);
}

// A view-space position (MODELVIEW_MATRIX * VERTEX) moved where the round Earth draws its world point
// `world`, as seen from `eye`: lowered by the drop, and from 20 km up also drawn in onto the sphere.
vec4 curved_view(vec4 view, vec3 world, vec3 eye, mat4 view_matrix) {
    view.xyz += mat3(view_matrix) * curve_offset(world, eye);
    return view;
}

// The same on a clip-space position: the flat product PROJECTION_MATRIX * MODELVIEW_MATRIX * v less the
// projected drop. Flat (world_curvature 0) it is that product bit for bit; P * (MV * v) rounds differently
// and speckled grass and leaves in the Realistic style (0.65 % of a near shot's pixels, #187).
vec4 curved_clip(vec4 clip, vec3 world, vec3 eye, mat4 view_matrix, mat4 projection) {
    return clip + projection * vec4(mat3(view_matrix) * curve_offset(world, eye), 0.0);
}

// As a vertex() writes them: POSITION = CURVED_CLIP(PROJECTION_MATRIX * MODELVIEW_MATRIX * v, world_pos), or
// CURVED on a view-space position that is worked on further (the road's far lift, the lamps' pull to the eye).
#define CURVED_CLIP(clip, world) curved_clip(clip, world, MAIN_CAM_INV_VIEW_MATRIX[3].xyz, VIEW_MATRIX, PROJECTION_MATRIX)
#define CURVED(view, world) curved_view(view, world, MAIN_CAM_INV_VIEW_MATRIX[3].xyz, VIEW_MATRIX)
// In fragment(), which has no MAIN_CAM_INV_VIEW_MATRIX and only runs for the view's own camera (a
// shadow pass has no fragment of ours), round CAMERA_POSITION_WORLD: the same point.
#define CURVED_FRAGMENT(view, world) curved_view(view, world, CAMERA_POSITION_WORLD, VIEW_MATRIX)
#define UNCURVED_FRAGMENT(world) uncurve(world, CAMERA_POSITION_WORLD)

// A world point rebuilt from the depth buffer (it was drawn curved), put back where the flat world has it.
vec3 uncurve(vec3 world, vec3 eye) {
    // the offset is all but the same at the drawn point as at the flat one (within mm near water)
    return world - curve_offset(world, eye);
}
#endif

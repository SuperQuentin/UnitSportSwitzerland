// The Earth's curvature, drawn round the camera (#187, docs/notes/world/earth-curvature.md): the
// world stays flat LV95 on the CPU; a body lowers what it draws by the drop at its flat distance from
// the camera, d² · world_curvature (1 / 2R, Core/Curvature: 246 m at 60 km; 0 = flat, the model viewer).
// Only POSITION curves: VERTEX and world positions stay flat, so normals, patterns, portal clips and
// the horizon cover are as before. Measured from the view's own main camera (MAIN_CAM_INV_VIEW_MATRIX:
// the same in its shadow pass), so a mirror, a portal or a preview viewport drops round its own eye.
#ifndef COMMON_CURVATURE
#define COMMON_CURVATURE
global uniform float world_curvature;

// A view-space position (MODELVIEW_MATRIX * VERTEX) lowered along the world's up by the Earth's drop
// at its world point `world`, as seen from `eye`.
vec4 curved_view(vec4 view, vec3 world, vec3 eye, mat4 view_matrix) {
    vec2 d = world.xz - eye.xz;
    view.xyz -= view_matrix[1].xyz * (dot(d, d) * world_curvature);
    return view;
}

// The two as a vertex() writes them: POSITION = PROJECTION_MATRIX * CURVED(MODELVIEW_MATRIX * v, world_pos).
#define CURVED(view, world) curved_view(view, world, MAIN_CAM_INV_VIEW_MATRIX[3].xyz, VIEW_MATRIX)
// In fragment(), which has no MAIN_CAM_INV_VIEW_MATRIX and only runs for the view's own camera (a
// shadow pass has no fragment of ours), round CAMERA_POSITION_WORLD: the same point.
#define CURVED_FRAGMENT(view, world) curved_view(view, world, CAMERA_POSITION_WORLD, VIEW_MATRIX)
#define UNCURVED_FRAGMENT(world) uncurve(world, CAMERA_POSITION_WORLD)

// A world point rebuilt from the depth buffer (it was drawn curved), put back where the flat world has it.
vec3 uncurve(vec3 world, vec3 eye) {
    vec2 d = world.xz - eye.xz;
    world.y += dot(d, d) * world_curvature;
    return world;
}
#endif

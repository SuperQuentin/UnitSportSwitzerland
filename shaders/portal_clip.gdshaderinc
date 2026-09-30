// Door portals (Interiors/DoorPortals): a portal camera stands where the viewer would be on the
// far side of a doorway, and that spot is rarely empty. Looking out, it is inside the building's
// closed shell; looking in, it is in the space under the terrain that every interior shares, often
// inside another building's rooms. So each portal camera, and only it, must not draw anything on
// its own side of the doorway it looks through: the plane keeps what is beyond it.
// Which camera is drawing is told by its position; each portal camera owns one slot (its eye and
// its doorway's plane), and an unused slot's eye is parked a long way off. Four slots: as many
// portal cameras as DoorPortals renders at once. A portal camera can land exactly where the
// screen's camera is (in at one door and out at another in line with it maps a camera onto
// itself), so DoorPortals nudges each one 5 cm or more back along its view, which no picture shows and
// this 2 cm test tells apart even tens of km from the origin, where a float step is ~2 mm.
global uniform vec3 portal_clip_eye_0;
global uniform vec4 portal_clip_plane_0; // xyz toward what is kept, w = dot(normal, a point on the plane)
global uniform vec3 portal_clip_eye_1;
global uniform vec4 portal_clip_plane_1;
global uniform vec3 portal_clip_eye_2;
global uniform vec4 portal_clip_plane_2;
global uniform vec3 portal_clip_eye_3;
global uniform vec4 portal_clip_plane_3;

bool portal_clip_slot(vec3 world, vec3 camera, vec3 eye, vec4 plane) {
    return distance(camera, eye) < 0.02 && dot(world, plane.xyz) < plane.w;
}

bool portal_clipped(vec3 world, vec3 camera) {
    return portal_clip_slot(world, camera, portal_clip_eye_0, portal_clip_plane_0)
        || portal_clip_slot(world, camera, portal_clip_eye_1, portal_clip_plane_1)
        || portal_clip_slot(world, camera, portal_clip_eye_2, portal_clip_plane_2)
        || portal_clip_slot(world, camera, portal_clip_eye_3, portal_clip_plane_3);
}

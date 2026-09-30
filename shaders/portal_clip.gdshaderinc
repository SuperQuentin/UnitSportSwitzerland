// Door portals (Interiors/DoorPortals): the street seen from inside a building is rendered by a
// second camera standing where the viewer would be if the interior were really under its roof,
// that is INSIDE the building's closed shell. That camera, and only that one, must not draw
// anything behind the doorway's plane: the shell's own walls, the ground under the floor.
// Which camera is drawing is told by its position; the portal camera is the only one that sits
// on portal_clip_eye, and between portals the eye is parked a long way off.
global uniform vec3 portal_clip_eye;
global uniform vec4 portal_clip_plane; // xyz outward normal, w = dot(normal, a point on the plane)

bool portal_clipped(vec3 world, vec3 camera) {
    return distance(camera, portal_clip_eye) < 0.05 && dot(world, portal_clip_plane.xyz) < portal_clip_plane.w;
}

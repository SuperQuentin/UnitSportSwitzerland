// Light spilling out of open front doors at night (src/Interiors/DoorLights, written every frame).
// Four slots, the open doors nearest the camera: xyz is the lamp's world position, just inside the
// doorway, behind the facade's plane; w is how much it shines, 0..1 (how far open × night). All
// zero by day and with every door shut, and door_light() then adds nothing.

global uniform vec4 world_door_light_0;
global uniform vec4 world_door_light_1;
global uniform vec4 world_door_light_2;
global uniform vec4 world_door_light_3;

// the room's lamps, linear, yellow enough to stay warm on snow under a blue night tint; the reach
// matches DoorLights.Reach (its OmniLight3D range); the gain makes the step about as bright as the
// room, the pool gone a few metres out
const vec3 DOOR_LAMP = vec3(1.0, 0.76, 0.46);
const float DOOR_REACH = 7.0;
const float DOOR_GAIN = 2.2;

float door_pool(vec4 lamp, vec3 p, vec3 n) {
    if (lamp.w <= 0.0) {
        return 0.0;
    }
    vec3 to = lamp.xyz - p;
    float dist = length(to);
    float fall = 1.0 - clamp(dist / DOOR_REACH, 0.0, 1.0);
    // only what faces the lamp: the step, the ground in front, the jambs; not the facade beside the
    // door, whose plane the lamp is behind
    return lamp.w * DOOR_GAIN * fall * fall * clamp(dot(n, to / max(dist, 0.001)), 0.0, 1.0);
}

// Added to world_tint: `col *= world_tint + door_light(world_pos, n)`, n the world normal.
vec3 door_light(vec3 p, vec3 n) {
    float a = door_pool(world_door_light_0, p, n) + door_pool(world_door_light_1, p, n)
        + door_pool(world_door_light_2, p, n) + door_pool(world_door_light_3, p, n);
    return DOOR_LAMP * a;
}

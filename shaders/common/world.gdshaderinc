// Time of day, set once per frame for the whole world by World/DayNight: the sun's direction,
// the colour of the light, the sky the fog fades into (linear), and 0 day .. 1 night. Every
// style's shader for a role reads the same globals (docs/notes/styles/role-bodies.md).
#ifndef COMMON_WORLD
#define COMMON_WORLD
global uniform vec3 world_sun_dir;
global uniform vec3 world_tint;
global uniform vec3 world_sky;
global uniform float world_night;
#endif

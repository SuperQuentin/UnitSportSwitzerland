// The floating origin (#185): world space moves to stay near the camera, so a pattern drawn
// from raw world XZ would jump at every shift. Patterns, hashes and waves read pattern_xz()
// instead: world XZ plus the origin's position modulo OriginShifter.PatternPeriodM, which is
// fixed to the ground and stays small enough to be precise.
global uniform vec2 world_origin_offset;

vec2 pattern_xz(vec3 world) {
    return world.xz + world_origin_offset;
}

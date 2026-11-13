
// The floating origin (#185): world space moves to stay near the camera, so a pattern drawn
// from raw world XZ would jump at every shift. Patterns, hashes and waves read pattern_xz()
// instead: world XZ plus the origin's position modulo OriginShifter.PatternPeriodM, which is
// fixed to the ground and stays small enough to be precise.
//
// On the round world (#1512, G2 C3) the frame turns at every re-anchor, which no offset follows:
// a leaf's meshes carry their pattern points instead (Terrain/Earth/LeafPattern), each vertex's
// cube-face (u, v) in metres. CUSTOM1 = face A's point (xy) and face B's (zw), CUSTOM2.x = 1 (0: a
// mesh with no pattern points, the flat world's, which keeps world_origin_offset), CUSTOM2.y and .z
// the angle of A's and B's pattern X axis in the mesh's frame (x towards z), CUSTOM2.w how far past
// A's edge towards B, metres (B's weight is a smoothstep of it, PatternSpace.WeightAt).
// A body's vertex() runs PATTERN_VERTEX first. B is another face, blended in within 60 m of a
// face edge: a fragment picks one by a noise on A's point, so the seam is ragged, never a line.
global uniform vec2 world_origin_offset;

varying vec4 pattern_ab;
varying vec4 pattern_frame;

#define PATTERN_VERTEX pattern_ab = CUSTOM1; pattern_frame = CUSTOM2;
#define PATTERN_NONE pattern_ab = vec4(0.0); pattern_frame = vec4(0.0);

// 0..1, steady on 8 m cells of A's point; periodic in 9.6 km (1200 cells), as A's point is
float pattern_seam_noise(vec2 p) {
    vec2 c = floor(p / 8.0);
    vec2 f = fract(p / 8.0);
    f = f * f * (3.0 - 2.0 * f);
    vec4 k = mod(vec4(c, c + 1.0), 1200.0);
    vec4 h = fract(sin(vec4(dot(k.xy, vec2(12.9898, 78.233)), dot(k.zy, vec2(12.9898, 78.233)),
        dot(k.xw, vec2(12.9898, 78.233)), dot(k.zw, vec2(12.9898, 78.233)))) * 43758.5453);
    return mix(mix(h.x, h.y, f.x), mix(h.z, h.w, f.x), f.y);
}

// Face B's weight here, 0..1 (0 off a face edge).
float pattern_weight_b(vec4 frame) {
    return smoothstep(-60.0, 60.0, frame.w);
}

// True where this fragment draws face B's pattern.
bool pattern_on_b(vec4 ab, vec4 frame) {
    float w = pattern_weight_b(frame);
    return w > 0.0 && pattern_seam_noise(ab.xy) < w;
}

vec2 pattern_xz(vec3 world) {
    if (pattern_frame.x < 0.5) {
        return world.xz + world_origin_offset;
    }
    return pattern_on_b(pattern_ab, pattern_frame) ? pattern_ab.zw : pattern_ab.xy;
}

// A pattern-space direction (x, z) in the mesh's frame: identity in the flat world.
vec2 pattern_turn_by(vec2 v, float angle) {
    float c = cos(angle);
    float s = sin(angle);
    return vec2(v.x * c - v.y * s, v.x * s + v.y * c);
}

vec2 pattern_turn(vec2 v) {
    if (pattern_frame.x < 0.5) {
        return v;
    }
    return pattern_turn_by(v, pattern_on_b(pattern_ab, pattern_frame) ? pattern_frame.z : pattern_frame.y);
}

// Ski slopes on the terrain (#887, World/Snow/PisteLayer). Each tile with slopes near the camera
// has a layer of piste_map, 2 m a texel from its NW corner: r = the slope's index in the tile,
// g = how far inside its edge (0 at the edge, 1 three metres in), b and a = the groom phase (the
// point's offset across the run, modulo 40 m) as a cosine and sine, so it filters without wrapping. piste_state holds three texels per slope,
// row layer*3 + k, column = the slope's index: k = 0 its snow at the low altitude and k = 1 at
// the high one (fresh corduroy, loose snow, ice, slush), k = 2 (moguls, low and high altitude / 4700).

uniform bool use_pistes = false;
uniform sampler2DArray piste_map : filter_linear, repeat_disable;
uniform sampler2D piste_state : filter_nearest, repeat_disable;
instance uniform int piste_layer = -1;
// corduroy ribs and groomer lanes: both divide the 40 m phase, so its wrap never breaks the pattern
const float PISTE_PHASE = 40.0;
uniform float piste_rib = 1.25;
uniform float piste_lane = 5.0;

float piste_hash(vec2 p) {
    return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

float piste_noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(piste_hash(i), piste_hash(i + vec2(1.0, 0.0)), f.x),
               mix(piste_hash(i + vec2(0.0, 1.0)), piste_hash(i + vec2(1.0, 1.0)), f.x), f.y);
}

// How much of a slope covers a tile-local point (its edge mask), 0 off one.
float piste_cover(vec2 local_xz) {
    if (!use_pistes || piste_layer < 0) {
        return 0.0;
    }
    return texture(piste_map, vec3((local_xz * 0.5 + 0.5) / 512.0, float(piste_layer))).g;
}

// The slope's snow over `col` at a tile-local point (x east, z south, metres) and its altitude.
// `rough` comes back lower on ice and slush for the lit styles. `sun` is the light direction.
vec3 piste_snow(vec3 col, vec2 local_xz, float altitude, vec3 n, vec3 sun, inout float rough) {
    // metres a pixel spans: taken before any branch, where derivatives are still defined
    float px = length(fwidth(local_xz));
    if (!use_pistes || piste_layer < 0) {
        return col;
    }
    vec2 tx = local_xz * 0.5;
    vec4 m = texture(piste_map, vec3((tx + 0.5) / 512.0, float(piste_layer)));
    if (m.g < 0.004) {
        return col;
    }
    ivec2 cell = clamp(ivec2(floor(tx + 0.5)), ivec2(0), ivec2(511));
    int slot = int(texelFetch(piste_map, ivec3(cell, piste_layer), 0).r * 255.0 + 0.5);
    if (slot == 0) {
        return col;
    }
    vec4 low = texelFetch(piste_state, ivec2(slot, piste_layer * 3), 0);
    vec4 high = texelFetch(piste_state, ivec2(slot, piste_layer * 3 + 1), 0);
    vec4 extra = texelFetch(piste_state, ivec2(slot, piste_layer * 3 + 2), 0);
    float alt_low = extra.g * 4700.0, alt_high = extra.b * 4700.0;
    vec4 st = mix(low, high, clamp((altitude - alt_low) / max(1.0, alt_high - alt_low), 0.0, 1.0));
    float fresh = st.r, loose = st.g, ice = st.b, wet = st.a, bumps = extra.r;


    // metres across the run, 0..40: the corduroy runs along the slope
    float across = (atan(m.a * 2.0 - 1.0, m.b * 2.0 - 1.0) / 6.2831853 + 0.5) * PISTE_PHASE;

    // the lit styles' sun takes this past 1 and a groomed run would be one white blot: darker there
#ifdef STYLE_LIT
    vec3 snow = vec3(0.30, 0.33, 0.38);
    float k = 0.42;
#else
    vec3 snow = vec3(0.80, 0.84, 0.90);
    float k = 1.0;
#endif
    // corduroy: fine ribs across the groomer's width, a seam between lanes; gone before they alias
    float legible = clamp(1.0 - px / 0.35, 0.0, 1.0);
    float rib = abs(fract(across / piste_rib) - 0.5) * 2.0;
    float seam = 1.0 - smoothstep(0.0, 0.06, abs(fract(across / piste_lane) - 0.5) * 2.0 - 0.94);
    // strong enough to survive the cel light and the PS1 posterising, which flatten small steps
    snow *= 1.0 - fresh * legible * (0.30 * smoothstep(0.2, 0.8, rib) + 0.18 * (1.0 - seam));
    // far off the ribs merge: a groomed run reads a touch bluer than skied-off snow
    snow *= mix(vec3(1.0), vec3(0.94, 0.96, 1.0), fresh * (1.0 - legible));
    // loose snow: brighter and lumpy
    float lump = piste_noise(local_xz / 1.7);
    snow = mix(snow, vec3(0.90, 0.93, 0.97) * k * (0.88 + 0.24 * lump), loose);
    // slush is lumpy too, wet and dark between the lumps
    snow *= 1.0 - wet * 0.15 * piste_noise(local_xz / 0.8);
    // moguls: bumps a few metres apart, their sunny and shady sides
    float mog = piste_noise(local_xz / 2.2) - piste_noise(local_xz / 2.2 + vec2(0.35, -0.35));
    snow *= 1.0 + bumps * 0.6 * mog;
    // ice: grey-blue and glossy; slush: dull and grey-brown
    snow = mix(snow, vec3(0.42, 0.52, 0.64) * k, ice * 0.7);
    snow = mix(snow, vec3(0.46, 0.45, 0.41) * k, wet * 0.75);
    // only ice shines: wet snow lit glossy turns white again at a grazing angle
    rough = mix(rough, 0.35, ice * m.g);
    // a sparse glitter on fresh and loose snow where it faces the sun
    float glint = step(0.997, piste_hash(floor(local_xz * 3.0))) * max(0.0, dot(n, sun)) * (fresh + loose) * legible;
    snow += vec3(0.35) * k * glint;
    return mix(col, snow, m.g);
}

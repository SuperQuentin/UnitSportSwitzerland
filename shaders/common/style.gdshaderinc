// The lit styles' finish, for the role bodies (docs/notes/styles/role-bodies.md). A wrapper
// defines them before including its body:
//   STYLE_LIT      Godot lights the surface: the body writes ALBEDO, NORMAL and EMISSION instead
//                  of lighting itself from world_sun_dir and world_tint
//   STYLE_CARTOON  (with STYLE_LIT) a cel light(): one soft terminator, sun shadows as the same
//                  band, a faint rim; colours lifted toward pastel (style_grade)
// Without STYLE_LIT a body keeps its unshaded PS1 path, and its retro finish follows `retro`.
#ifndef COMMON_STYLE
#define COMMON_STYLE

// A world-space facet normal as the view-space NORMAL a lit fragment writes.
vec3 style_view_normal(mat4 view, vec3 n_world) {
    return normalize((view * vec4(n_world, 0.0)).xyz);
}

#ifdef STYLE_CARTOON
uniform float toon_edge = 0.08;   // width of the light/shade terminator, in N.L
uniform float toon_rim = 0.22;    // rim strength on the lit side

// Breath-of-the-Wild-like: flat facets kept, one soft-edged terminator, the shade side left to
// the cool, bluish ambient (Styles/StyleKit drives it), sun shadows read as the same band.
void light() {
    float ndl = dot(NORMAL, LIGHT);
    // A face nearly edge-on to the sun self-shadows in stripes (acne), which the hard band
    // turns into hatching: near the terminator, N.L alone shades it.
    float shadow = mix(1.0, smoothstep(0.3, 0.6, ATTENUATION), smoothstep(0.1, 0.35, ndl));
    float lit = smoothstep(0.0, toon_edge, ndl) * shadow;
    DIFFUSE_LIGHT += lit * LIGHT_COLOR / PI;
    float rim = pow(1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0), 4.0);
    SPECULAR_LIGHT += smoothstep(0.45, 0.55, rim) * lit * toon_rim * LIGHT_COLOR / PI * ALBEDO;
}

// Toward pastel: greens lean yellow (BotW's meadows are olive-gold, not emerald), a touch more
// light and a touch less saturation everywhere.
vec3 style_grade(vec3 col) {
    float green = max(col.g - max(col.r, col.b), 0.0);
    col.r += green * 0.45;
    col.b -= green * 0.15;
    float l = dot(col, vec3(0.2126, 0.7152, 0.0722));
    col = mix(vec3(l), col, 0.92);
    return pow(max(col, vec3(0.0)), vec3(0.92)) * 0.95;
}

// Low-frequency value noise, 0..1: the painted variation BotW has on every large surface.
float style_paint_noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = fract(sin(dot(i, vec2(127.1, 311.7))) * 43758.5453);
    float b = fract(sin(dot(i + vec2(1.0, 0.0), vec2(127.1, 311.7))) * 43758.5453);
    float c = fract(sin(dot(i + vec2(0.0, 1.0), vec2(127.1, 311.7))) * 43758.5453);
    float d = fract(sin(dot(i + vec2(1.0, 1.0), vec2(127.1, 311.7))) * 43758.5453);
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}
#else
vec3 style_grade(vec3 col) {
    return col;
}
#endif
#endif

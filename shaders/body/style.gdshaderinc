// Visual style switches for the role bodies in shaders/body/ (prototype, issue #181).
//
// A role's shader file is a thin wrapper: shader_type, render_mode, one or more STYLE_* defines,
// then the body include. The body keeps every functional part (portal clip, door light,
// occasions, patterns, markings, windows) shared, and only the finishing differs:
//   STYLE_PS1      unshaded: vertex snap, stepped sun, tint, Bayer dither + posterize
//   STYLE_LIT      Godot does the lighting: the body writes ALBEDO / NORMAL / ROUGHNESS / EMISSION
//   STYLE_CARTOON  (with STYLE_LIT) a cel light() — one hard terminator, shadows as a band, rim
//   STYLE_REAL     (with STYLE_LIT) the standard PBR light, textured surfaces

// Clip-space position, snapped to a 640x480 grid in PS1 only.
vec4 style_clip(vec4 clip, vec2 snap_resolution) {
#ifdef STYLE_PS1
    if (clip.w > 0.0) {
        vec2 half_res = snap_resolution * 0.5;
        vec2 ndc = clip.xy / clip.w;
        ndc = floor(ndc * half_res + 0.5) / half_res;
        clip.xy = ndc * clip.w;
    }
#endif
    return clip;
}

// PS1 4x4 Bayer dither + posterize.
vec3 style_posterize(vec3 col, vec2 fragcoord, float color_levels) {
    const float bayer[16] = float[16](
        0.0, 8.0, 2.0, 10.0,
        12.0, 4.0, 14.0, 6.0,
        3.0, 11.0, 1.0, 9.0,
        15.0, 7.0, 13.0, 5.0);
    int bi = int(mod(fragcoord.x, 4.0)) + 4 * int(mod(fragcoord.y, 4.0));
    float dith = (bayer[bi] / 16.0 - 0.5) / color_levels;
    return floor((col + dith) * color_levels + 0.5) / color_levels;
}

// world -> view space normal, for writing NORMAL from a world-space facet normal
vec3 style_view_normal(mat4 view, vec3 n_world) {
    return normalize((view * vec4(n_world, 0.0)).xyz);
}

// --- cartoon ------------------------------------------------------------------------------
// Breath-of-the-Wild-like: flat facets kept, one soft-edged terminator, the shade side left to
// the (cool, bluish) ambient, sun shadows read as the same band, a faint rim on the lit side.
// Colours are lifted toward pastel in the fragment (style_cartoon_grade).
#ifdef STYLE_CARTOON
uniform float toon_edge = 0.08;      // width of the light/shade terminator (in N.L)
uniform float toon_rim = 0.22;       // rim strength on the lit side

void light() {
    float ndl = dot(NORMAL, LIGHT);
    float lit = smoothstep(0.0, toon_edge, ndl) * smoothstep(0.3, 0.6, ATTENUATION);
    DIFFUSE_LIGHT += lit * LIGHT_COLOR / PI;
    float rim = pow(1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0), 4.0);
    SPECULAR_LIGHT += smoothstep(0.45, 0.55, rim) * lit * toon_rim * LIGHT_COLOR / PI * ALBEDO;
}

// toward pastel: a touch more light, a touch less saturation, warmer greens
vec3 style_cartoon_grade(vec3 col) {
    // greens lean yellow (BotW's meadows are olive-gold, not emerald), everything a bit softer
    float green = max(col.g - max(col.r, col.b), 0.0);
    col.r += green * 0.45;
    col.b -= green * 0.15;
    float l = dot(col, vec3(0.2126, 0.7152, 0.0722));
    col = mix(vec3(l), col, 0.92);
    return pow(max(col, vec3(0.0)), vec3(0.92)) * 0.95;
}

// low-frequency world-space mottling, the "painted" variation BotW has on every large surface
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
#endif

// --- realistic ----------------------------------------------------------------------------
#ifdef STYLE_REAL
// Detail texture tinted by the baked vertex colour: the texture brings the grain, the vertex
// colour keeps the land cover readable (and matches every other style's palette). The texture's
// own average is its last mip, so a texture divided by it averages to 1.
vec3 style_tinted(sampler2D tex, vec2 uv, vec3 vcol, float weight) {
    vec3 t = texture(tex, uv).rgb;
    vec3 mean = textureLod(tex, vec2(0.5), 12.0).rgb;
    return mix(vcol * t / max(mean, vec3(0.02)), t, weight);
}
#endif

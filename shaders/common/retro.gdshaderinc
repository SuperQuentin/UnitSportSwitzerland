// The PS1 finish: the sun in steps, Bayer dither + posterize. The vertex snap is gone (#788): it
// moved each vertex up to half a snap pixel on screen, so faces centimetres apart traded places
// at grazing angles (roofs, kerbs, overlays) and everything wobbled; the game is no longer
// strictly PS1. retro_snap stays as a pass-through for the shaders that call it.
// Behind one uniform, so a PS1 shader that another style borrows looks neutral there instead of
// retro: Styles/StyleKit turns `retro` off on every material that resolves to a PS1 shader for
// another style (docs/notes/styles/role-bodies.md). With it on, the results are exactly the old
// inline code's.
//
// The 4x4 Bayer threshold is also what the dissolves (sightline cut, near fade, tree LOD
// crossfade) discard by. Those are functional, not a finish: they keep it whatever `retro` says.
#ifndef COMMON_RETRO
#define COMMON_RETRO
uniform bool retro = true;
// In a headset (#244, set by XR/XrSession): no dither, noise the Link video encoder turns into
// blocks. The colour steps stay.
global uniform bool xr_smooth;
uniform float color_levels = 16.0;

// Clip-space position, unchanged: the PS1 vertex snap was dropped (#788, see above).
vec4 retro_snap(vec4 clip) {
    return clip;
}

// A light term 0..1 in `levels` hard steps, or left smooth.
float retro_steps(float x, float levels) {
    return retro ? floor(x * levels) / levels : x;
}

// This pixel's threshold in the 4x4 Bayer matrix: 0, 1/16 .. 15/16.
float bayer4(vec2 fragcoord) {
    const float bayer[16] = float[16](
        0.0, 8.0, 2.0, 10.0,
        12.0, 4.0, 14.0, 6.0,
        3.0, 11.0, 1.0, 9.0,
        15.0, 7.0, 13.0, 5.0);
    return bayer[int(mod(fragcoord.x, 4.0)) + 4 * int(mod(fragcoord.y, 4.0))] / 16.0;
}

// Bayer dither + posterize to color_levels per channel.
vec3 retro_posterize(vec3 col, vec2 fragcoord) {
    if (!retro) {
        return col;
    }
    float dith = xr_smooth ? 0.0 : (bayer4(fragcoord) - 0.5) / color_levels;
    return floor((col + dith) * color_levels + 0.5) / color_levels;
}
#endif

// The PS1 finish: vertex snap to a 640x480 grid, the sun in steps, Bayer dither + posterize.
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
uniform vec2 snap_resolution = vec2(640.0, 480.0);
uniform float color_levels = 16.0;

// Clip-space position, snapped to the snap_resolution grid.
vec4 retro_snap(vec4 clip) {
    if (retro && clip.w > 0.0) {
        vec2 half_res = snap_resolution * 0.5;
        vec2 ndc = clip.xy / clip.w;
        ndc = floor(ndc * half_res + 0.5) / half_res;
        clip.xy = ndc * clip.w;
    }
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
    float dith = (bayer4(fragcoord) - 0.5) / color_levels;
    return floor((col + dith) * color_levels + 0.5) / color_levels;
}
#endif

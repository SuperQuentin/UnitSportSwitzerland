// --- sightline cut ----------------------------------------------------------------------
// Dissolves anything standing between a camera and its subject. The fade is a Bayer-dither
// discard, not alpha blending: these shaders are unshaded and opaque, the trees are one
// MultiMesh sharing a single material, and a dithered dissolve is already this renderer's
// visual language. cut_radius = 0 switches the whole thing off.
#ifndef COMMON_SIGHTLINE
#define COMMON_SIGHTLINE
uniform vec3 cut_from = vec3(0.0);
uniform vec3 cut_to = vec3(0.0);
uniform float cut_radius = 0.0;
uniform float cut_softness = 1.6;

// How much of this fragment survives: 1 fully drawn, 0 fully dissolved.
float sightline_keep(vec3 p) {
    if (cut_radius <= 0.0) {
        return 1.0;
    }
    vec3 axis = cut_to - cut_from;
    float len2 = dot(axis, axis);
    if (len2 < 1.0) {
        return 1.0;
    }

    // where along the camera -> subject segment this fragment sits
    float t = dot(p - cut_from, axis) / len2;
    if (t <= 0.0 || t >= 1.0) {
        return 1.0;
    }

    float radial = length(p - (cut_from + axis * t));

    // Taper to nothing at both ends, so the runner's own feet and the ground immediately under
    // the lens are never eaten - only what is genuinely in between.
    float ends = smoothstep(0.0, 0.12, t) * smoothstep(0.0, 0.12, 1.0 - t);
    float core = 1.0 - smoothstep(cut_radius, cut_radius + cut_softness, radial);

    return 1.0 - core * ends;
}
#endif

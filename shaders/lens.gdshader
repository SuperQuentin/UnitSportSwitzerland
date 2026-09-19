shader_type canvas_item;

// Optical lens simulation for the replay camera: radial distortion, chromatic aberration
// and a vignette, applied to the finished frame.
//
// This warps an already-rendered perspective image, so it cannot manufacture field of view
// the renderer never drew — the widening comes from the paired FOV bias on the camera, and
// the curve comes from here. Together they read as a real lens. A true >=180 degree fisheye
// would need the scene rendered to a cube and resampled, which costs 3-5 draws of the whole
// world per frame and is not worth it for a look.
//
// The corner radius is normalised to 1 and the distortion renormalised by its own value at
// r = 1, so the corners always land exactly on the corners. That is what keeps the frame
// full at every strength instead of pulling black wedges in from the edges.

uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap;

uniform float k1 = 0.0;          // quadratic term; >0 barrel, <0 pincushion
uniform float k2 = 0.0;          // quartic term, for the corners
uniform float chromatic = 0.0;   // red/blue split, as a fraction of the displacement
uniform float vignette = 0.0;    // corner darkening, 0..1
uniform vec2 warp_axis = vec2(1.0, 1.0);   // (1,1) spherical; (1,0.15) anamorphic
uniform float aspect = 1.7777778;

// Maps an output pixel back to the source pixel it should be read from.
vec2 distort(vec2 uv, float strength) {
    vec2 p = (uv - 0.5) * 2.0;
    p.x *= aspect;

    float corner = sqrt(aspect * aspect + 1.0);
    float r = length(p) / corner;

    float f = 1.0 + k1 * strength * r * r + k2 * strength * r * r * r * r;
    float fmax = 1.0 + k1 * strength + k2 * strength;
    if (abs(fmax) < 0.001) {
        fmax = 0.001;
    }

    vec2 q = p + p * (f / fmax - 1.0) * warp_axis;
    q.x /= aspect;
    return q * 0.5 + 0.5;
}

void fragment() {
    vec2 uv = SCREEN_UV;

    vec3 col;
    if (chromatic > 0.0) {
        // Each channel is refracted by a slightly different amount, which is what a real lens
        // does and why the fringing is strongest at the edge of frame and absent at the centre.
        col.r = texture(screen_tex, distort(uv, 1.0 + chromatic)).r;
        col.g = texture(screen_tex, distort(uv, 1.0)).g;
        col.b = texture(screen_tex, distort(uv, 1.0 - chromatic)).b;
    } else {
        col = texture(screen_tex, distort(uv, 1.0)).rgb;
    }

    if (vignette > 0.0) {
        vec2 p = (uv - 0.5) * 2.0;
        p.x *= aspect;
        float r = length(p) / sqrt(aspect * aspect + 1.0);
        col *= 1.0 - vignette * smoothstep(0.35, 1.05, r);
    }

    COLOR = vec4(col, 1.0);
}

// The figures' material (HumanMeshBuilder.FigureMaterial, docs/notes/avatar/clothing.md): the
// body of its style wrappers (avatar, cartoon_avatar, real_avatar), each shader_type,
// render_mode, its defines, then this include. A plain surface is exactly what
// HumanMeshBuilder.Material gives in the same style: vertex colour as albedo, lit, no specular,
// roughness 1 (Cartoon: toon, rim; Realistic: GGX, 0.55), no normals of its own (MeshScratch
// writes none). Clothes may carry a finish in the vertex colour's ALPHA, 1 - id/255
// (Avatar.Garments.Fx; ids are Avatar.Finish):
//   1 rainbow, 2 disco ball, 3 galaxy, 4 holographic, 5 glitch, 6 lava, 7 neon,
//   8 tartan, 9 fishnet, 10 lace;
//   11 the procedural pixel face (#657, face.gdshaderinc): drawn at UV from the genome in UV2,
//      its clear pixels discarded so the head's skin behind shows through; the iris is the
//      vertex colour (the eye colour);
//   12 checker, 13 stripes, 14 studs (#394).
// Alpha 1 everywhere else, so every other mesh drawn with this is unchanged. Patterns are laid
// out where each point sits on the figure standing at rest (CUSTOM0, MeshScratch.Rest, #724), so
// they stay on the cloth while it runs, jumps or bends; a mesh without that channel (w = 0) falls
// back to its own space (origin at the feet).

#include "res://shaders/body/face.gdshaderinc"

varying vec3 v_pos;
varying flat int v_fx;

void vertex() {
    v_pos = CUSTOM0.w > 0.5 ? CUSTOM0.xyz : VERTEX;
    v_fx = int(round((1.0 - COLOR.a) * 255.0));
    v_face = UV2;
}

float hash13(vec3 p) {
    p = fract(p * 0.1031);
    p += dot(p, p.zyx + 31.32);
    return fract((p.x + p.y) * p.z);
}

float noise3(vec3 p) {
    vec3 i = floor(p);
    vec3 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(mix(hash13(i), hash13(i + vec3(1, 0, 0)), f.x),
                   mix(hash13(i + vec3(0, 1, 0)), hash13(i + vec3(1, 1, 0)), f.x), f.y),
               mix(mix(hash13(i + vec3(0, 0, 1)), hash13(i + vec3(1, 0, 1)), f.x),
                   mix(hash13(i + vec3(0, 1, 1)), hash13(i + vec3(1, 1, 1)), f.x), f.y), f.z);
}

vec3 hsv(float h, float s, float v) {
    vec3 k = clamp(abs(mod(h * 6.0 + vec3(0.0, 4.0, 2.0), 6.0) - 3.0) - 1.0, 0.0, 1.0);
    return v * mix(vec3(1.0), k, s);
}

// round a figure's limbs and body: around the vertical axis, so patterns wrap a tube
float around(vec3 p) {
    return atan(p.x, p.z) * 0.16 + (p.x + p.z) * 0.5;
}

void fragment() {
    vec3 base = COLOR.rgb;
    ALBEDO = base;
#if defined(STYLE_CARTOON)
    // StyleKit.Figure's Cartoon shading, as on the plain figures: toon light and a rim
    ROUGHNESS = 0.9;
    RIM = 0.35;
    RIM_TINT = 0.6;
#elif defined(STYLE_REAL)
    // and Realistic's: one GGX surface for paint, cloth and skin alike
    ROUGHNESS = 0.55;
#else
    ROUGHNESS = 1.0;
#endif
    if (v_fx == 0) {
        // the plain figure
    } else if (v_fx == 11) {
        vec4 face = face_px(UV, v_face, base, TIME);
        if (face.a < 0.5) {
            discard;
        }
        ALBEDO = face.rgb;
    } else if (v_fx >= 12 && v_fx <= 14) {
        vec3 p = v_pos;
        if (v_fx == 12) {
            // checker: squares the size of a hand's width, the colour and near black
            float c = mod(floor(around(p) * 30.0) + floor(p.y * 30.0), 2.0);
            ALBEDO = c < 0.5 ? base : base * 0.18;
        } else if (v_fx == 13) {
            // stripes round the body: a dark band in every three
            ALBEDO = fract(p.y * 16.0) < 0.36 ? base * 0.2 : base;
        } else {
            // studs: bright metal dots in a grid on the dark cloth
            vec2 g = fract(vec2(around(p) * 22.0, p.y * 22.0)) - 0.5;
            if (length(g) < 0.2) {
                ALBEDO = vec3(0.82, 0.82, 0.86);
                ROUGHNESS = 0.3;
            }
        }
    } else {
        // a facet's own normal (view space), for the effects that change with the angle
        vec3 fn = normalize(cross(dFdx(VERTEX), dFdy(VERTEX)));
        float facing = abs(dot(fn, VIEW));
        float t = TIME;
        vec3 p = v_pos;
        if (v_fx == 1) {
            // rainbow: hue bands climbing the figure
            vec3 c = hsv(fract(p.y * 1.6 - t * 0.45 + around(p) * 0.6), 0.85, 1.0);
            ALBEDO = c;
            EMISSION = c * 0.35;
        } else if (v_fx == 2) {
            // disco ball: little mirror tiles, each its own grey, flashing as the light swings round
            vec3 cell = floor(p * 26.0);
            vec3 f = fract(p * 26.0);
            float grout = step(0.12, min(min(f.x, f.y), f.z) + 0.06);
            float h = hash13(cell);
            float mirror = mix(0.45, 0.95, h);
            float sweep = pow(max(0.0, sin(t * 2.2 + h * 37.0 + p.y * 3.0)), 24.0);
            float sparkle = step(0.985, hash13(cell + floor(t * 7.0)));
            vec3 tint = hsv(fract(h * 3.0 + t * 0.2), 0.35, 1.0);
            ALBEDO = vec3(mirror) * mix(0.35, 1.0, grout);
            EMISSION = (tint * sweep * 2.2 + vec3(sparkle) * 3.0) * grout + vec3(mirror) * 0.12 * facing;
        } else if (v_fx == 3) {
            // galaxy: a nebula drifting through deep violet, stars that twinkle
            float n = noise3(p * 4.0 + vec3(0.0, t * 0.08, 0.0));
            float n2 = noise3(p * 9.0 - vec3(t * 0.05, 0.0, 0.0));
            vec3 deep = mix(vec3(0.03, 0.02, 0.10), vec3(0.20, 0.05, 0.38), n);
            deep = mix(deep, vec3(0.05, 0.30, 0.55), smoothstep(0.62, 0.9, n2) * 0.6);
            vec3 cell = floor(p * 55.0);
            float star = step(0.975, hash13(cell));
            float twinkle = 0.5 + 0.5 * sin(t * 4.0 + hash13(cell + 7.0) * 30.0);
            ALBEDO = deep;
            EMISSION = deep * 0.6 + vec3(star * twinkle * 2.5);
        } else if (v_fx == 4 || v_fx == 5) {
            // holographic: the hue follows the angle you see it at
            vec3 c = mix(vec3(0.85), hsv(fract(facing * 1.4 + p.y * 0.9 + t * 0.12), 0.55, 1.0), 0.85);
            ALBEDO = c;
            EMISSION = c * 0.25;
            if (v_fx == 5) {
                // glitch: now and then a band of the body jumps to the wrong colours
                float step_t = floor(t * 9.0);
                float band = floor(p.y * 22.0);
                if (hash13(vec3(band, step_t, 3.0)) > 0.86) {
                    vec3 bad = hash13(vec3(band, step_t, 9.0)) > 0.5 ? vec3(0.1, 1.0, 0.9) : vec3(1.0, 0.1, 0.7);
                    ALBEDO = bad;
                    EMISSION = bad * 1.4;
                }
                if (hash13(vec3(floor(p * 40.0).xy, step_t)) > 0.97) {
                    ALBEDO = vec3(0.0);
                    EMISSION = vec3(0.0);
                }
            }
        } else if (v_fx == 6) {
            // lava: black crust, glowing cracks that breathe
            float n = noise3(p * 7.0 + vec3(0.0, -t * 0.15, 0.0));
            float crack = 1.0 - smoothstep(0.0, 0.08, abs(n - 0.5));
            float pulse = 0.65 + 0.35 * sin(t * 2.0 + p.y * 4.0);
            vec3 glow = mix(vec3(1.0, 0.25, 0.02), vec3(1.0, 0.75, 0.15), crack);
            ALBEDO = mix(base * 0.6, glow, crack);
            EMISSION = glow * crack * pulse * 2.2;
        } else if (v_fx == 7) {
            // neon: the colour itself glows and pulses, shifting toward its opposite
            float pulse = 0.55 + 0.45 * sin(t * 4.0 + p.y * 6.0);
            vec3 c = mix(base, base.gbr, 0.5 + 0.5 * sin(t * 0.7));
            ALBEDO = c * 0.4;
            EMISSION = c * (1.2 + 1.6 * pulse);
        } else if (v_fx == 8) {
            // tartan: two dark bands crossing, a thin light line through each
            float u = fract(around(p) * 9.0);
            float v = fract(p.y * 9.0);
            float dark = (u < 0.34 ? 0.55 : 1.0) * (v < 0.34 ? 0.55 : 1.0);
            float line = (abs(u - 0.67) < 0.04 || abs(v - 0.67) < 0.04) ? 1.0 : 0.0;
            ALBEDO = mix(base * dark, vec3(0.08), (u < 0.34 && v < 0.34) ? 0.5 : 0.0);
            ALBEDO = mix(ALBEDO, vec3(0.85, 0.80, 0.70), line * 0.6);
        } else if (v_fx == 9) {
            // fishnet: a diamond mesh, the skin under it showing through the holes
            float a = fract((p.y + around(p) * 0.5) * 55.0);
            float b = fract((p.y - around(p) * 0.5) * 55.0);
            if (a > 0.22 && b > 0.22) {
                discard;
            }
        } else if (v_fx == 10) {
            // lace: rings of holes, flowers more or less
            vec3 q = p * vec3(70.0, 70.0, 70.0);
            vec2 g = fract(vec2(around(p) * 70.0, q.y)) - 0.5;
            float r = length(g);
            if (r < 0.28 || (r > 0.36 && r < 0.42 && hash13(floor(vec3(around(p) * 70.0, q.y, 1.0))) > 0.5)) {
                discard;
            }
        }
    }
}

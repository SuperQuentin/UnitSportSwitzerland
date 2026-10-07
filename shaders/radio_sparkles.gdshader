shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled, fog_disabled, shadows_disabled, skip_vertex_transform;

// Sparkles round a playing radio (src/Items/RadioSparkles, #387, #725). One shared mesh of a few
// dozen quads; each quad's four corners share their glint's spawn point (VERTEX, in the radio's
// own space), its corner (UV, -1..1) and two hashes (UV2). Every glint lives, rises, twinkles and
// dies here from TIME alone: no particle state, no CPU work per glint, one draw call per radio.
// About one in four is a music note instead of a star. The node sets a few instance uniforms from
// the CD's analysis on the shared clock: how loud the song is right now, its kicks, the bar's
// first beat, a burst when a new section starts, and how big the section is.

instance uniform float on = 0.0;      // 0..1: hidden..all glints at full size
instance uniform float pulse = 0.0;   // the kick: 1 on a hit, decaying (the beat grid when the CD has no envelope)
instance uniform float beat = 0.0;    // the beat number, mod 4
instance uniform float level = 1.0;   // 0..1 loudness right now: how many glints, how fast
instance uniform float bar = 0.0;     // 1 on a bar's first beat, decaying
instance uniform float burst = 0.0;   // 1 when a new section starts, decaying over a second or two
instance uniform float peak = 0.5;    // 0 calm .. 1 chorus: the section's size

uniform float rise = 0.32;            // m a glint climbs over its life
uniform float size = 0.034;           // m, half a glint's width at its peak

varying vec3 tint;
varying float glow;
varying float note;                   // 0 star, 1 a single quaver, 2 two beamed quavers

const vec3 PALETTE[4] = vec3[4](
    vec3(1.0, 0.82, 0.35),   // gold
    vec3(0.40, 0.88, 1.0),   // cyan
    vec3(1.0, 0.45, 0.85),   // pink
    vec3(0.95, 0.95, 1.0));  // white

void vertex() {
    float h = UV2.x, h2 = UV2.y;
    bool is_note = fract(h * 13.7) < 0.26;
    // louder parts live faster; a big section adds more of them
    float speed = 0.75 + 0.5 * level + 0.25 * peak;
    float life = (1.1 + 0.9 * h2) * (is_note ? 1.5 : 1.0);
    float age = fract(TIME * speed / life + h * 7.0);
    // how many show: a quiet passage keeps a handful, the chorus all of them
    float density = clamp(0.25 + 0.55 * level + 0.3 * peak + 0.6 * burst, 0.0, 1.0);
    float present = step(fract(h * 31.0), density);

    vec3 home = VERTEX;
    vec3 out_dir = normalize(vec3(home.x, 0.0, home.z) + vec3(1e-4));
    float kick = max(pulse, bar);
    vec3 pos = home
        + vec3(0.0, rise * age * (is_note ? 1.3 : 1.0) * (0.8 + 0.4 * peak), 0.0)
        + out_dir * (0.05 * age + 0.06 * kick + 0.14 * burst * (1.0 - age))
        + vec3(sin(TIME * (1.7 + h2) + h * 30.0), 0.0, cos(TIME * (1.3 + h) + h2 * 30.0)) * 0.025 * age;
    // a note wobbles side to side as it floats up
    if (is_note) {
        pos.x += sin(TIME * 4.0 + h * 40.0) * 0.03 * age;
    }

    // born small, peaking early, gone at the end; twinkling all along; bigger on a hit
    float env = smoothstep(0.0, 0.12, age) * (1.0 - smoothstep(0.55, 1.0, age));
    float twinkle = is_note ? 0.85 + 0.15 * sin(TIME * 6.0 + h * 20.0)
                            : 0.45 + 0.55 * abs(sin(TIME * (7.0 + 9.0 * h2) + h * 50.0));
    float grow = 1.0 + 0.9 * pulse + 0.7 * bar + 0.8 * burst;
    float s = size * (is_note ? 1.6 : 1.0) * env * twinkle * grow * on * present;

    vec3 view = (MODELVIEW_MATRIX * vec4(pos, 1.0)).xyz;
    view.xy += UV * s * length(MODEL_MATRIX[0].xyz);   // node scale: a radio on the back is smaller
    VERTEX = view;

    tint = PALETTE[int(mod(floor(h * 4.0) + beat, 4.0))];
    glow = (0.8 + 0.4 * level + 1.4 * pulse + 0.8 * bar) * twinkle;
    note = is_note ? (fract(h * 5.3) < 0.5 ? 1.0 : 2.0) : 0.0;
}

// coverage of a quaver (or two beamed ones) in the quad's -1..1 space, y up
float quaver(vec2 p, float pair) {
    // heads: tilted ellipses at the bottom; stems up their right; a flag or a beam on top
    mat2 tilt = mat2(vec2(0.87, 0.5), vec2(-0.5, 0.87));
    float x1 = pair > 1.5 ? -0.17 : 0.08;
    vec2 h1 = tilt * (p - vec2(x1 - 0.28, -0.55));
    float head = 1.0 - smoothstep(0.85, 1.0, length(h1 * vec2(2.6, 3.8)));
    float stem = step(abs(p.x - x1), 0.06) * step(-0.5, p.y) * step(p.y, 0.75);
    float mark;
    if (pair > 1.5) {
        vec2 h2 = tilt * (p - vec2(0.38, -0.45));
        head = max(head, 1.0 - smoothstep(0.85, 1.0, length(h2 * vec2(2.6, 3.8))));
        stem = max(stem, step(abs(p.x - 0.66), 0.06) * step(-0.4, p.y) * step(p.y, 0.85));
        float beam_y = mix(0.66, 0.78, (p.x - x1) / (0.66 - x1));
        mark = step(x1 - 0.06, p.x) * step(p.x, 0.72) * step(abs(p.y - beam_y), 0.1);
    } else {
        // the flag: a curve off the stem's top, down and to the right
        float fx = p.x - x1;
        float fy = 0.75 - fx * 1.1 - fx * fx * 0.6;
        mark = step(0.0, fx) * step(fx, 0.5) * step(abs(p.y - fy), 0.1);
    }
    return clamp(max(head, max(stem, mark)), 0.0, 1.0);
}

void fragment() {
    vec2 p = UV;
    float a;
    float core;
    if (note > 0.5) {
        a = quaver(p, note);   // UV's y is view-space up: the vertex stage spreads the corners by it
        core = 0.3 * a;
    } else {
        float d2 = dot(p, p);
        // a bright core and a four-pointed cross of rays: a star glint
        core = exp(-d2 * 10.0);
        float rays = exp(-abs(p.x) * 16.0) * (1.0 - abs(p.y)) + exp(-abs(p.y) * 16.0) * (1.0 - abs(p.x));
        a = clamp(core + 0.85 * rays, 0.0, 1.0);
    }
    if (a < 0.01) {
        discard;
    }
    ALBEDO = mix(tint, vec3(1.0), core * 0.6) * glow;
    ALPHA = a;
}

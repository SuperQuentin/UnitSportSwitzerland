shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled, fog_disabled, shadows_disabled, skip_vertex_transform;

// Sparkles round a playing radio (src/Items/RadioSparkles, #387). One shared mesh of a few dozen
// quads; each quad's four corners share their glint's spawn point (VERTEX, in the radio's own
// space), its corner (UV, -1..1) and two hashes (UV2). Every glint lives, rises, twinkles and dies
// here from TIME alone: no particle state, no CPU work per glint, one draw call per radio. The node
// sets three instance uniforms: how much it shows (fading in and out), the beat's kick, and the
// beat number (colours step round the palette on each beat).

instance uniform float on = 0.0;     // 0..1: hidden..all glints at full size
instance uniform float pulse = 0.0;  // 1 on the beat, decaying to 0 before the next
instance uniform float beat = 0.0;   // the beat number, mod 4

uniform float rise = 0.32;           // m a glint climbs over its life
uniform float size = 0.034;          // m, half a glint's width at its peak

varying vec3 tint;
varying float glow;

const vec3 PALETTE[4] = vec3[4](
    vec3(1.0, 0.82, 0.35),   // gold
    vec3(0.40, 0.88, 1.0),   // cyan
    vec3(1.0, 0.45, 0.85),   // pink
    vec3(0.95, 0.95, 1.0));  // white

void vertex() {
    float h = UV2.x, h2 = UV2.y;
    float life = 1.1 + 0.9 * h2;
    float age = fract(TIME / life + h * 7.0);
    // out from the radio and up, swaying; pushed outward a little on every beat
    vec3 home = VERTEX;
    vec3 out_dir = normalize(vec3(home.x, 0.0, home.z) + vec3(1e-4));
    vec3 pos = home
        + vec3(0.0, rise * age, 0.0)
        + out_dir * (0.05 * age + 0.05 * pulse)
        + vec3(sin(TIME * (1.7 + h2) + h * 30.0), 0.0, cos(TIME * (1.3 + h) + h2 * 30.0)) * 0.025 * age;

    // born small, peaking early, gone at the end; twinkling all along; bigger on the beat
    float env = smoothstep(0.0, 0.12, age) * (1.0 - smoothstep(0.55, 1.0, age));
    float twinkle = 0.45 + 0.55 * abs(sin(TIME * (7.0 + 9.0 * h2) + h * 50.0));
    float s = size * env * twinkle * (1.0 + 0.9 * pulse) * on;

    vec3 view = (MODELVIEW_MATRIX * vec4(pos, 1.0)).xyz;
    view.xy += UV * s * length(MODEL_MATRIX[0].xyz);   // node scale: a radio on the back is smaller
    VERTEX = view;

    tint = PALETTE[int(mod(floor(h * 4.0) + beat, 4.0))];
    glow = (0.9 + 1.4 * pulse) * twinkle;
}

void fragment() {
    vec2 p = UV;
    float d2 = dot(p, p);
    // a bright core and a four-pointed cross of rays: a star glint
    float core = exp(-d2 * 10.0);
    float rays = exp(-abs(p.x) * 16.0) * (1.0 - abs(p.y)) + exp(-abs(p.y) * 16.0) * (1.0 - abs(p.x));
    float a = clamp(core + 0.85 * rays, 0.0, 1.0);
    if (a < 0.01) {
        discard;
    }
    ALBEDO = mix(tint, vec3(1.0), core * 0.6) * glow;
    ALPHA = a;
}

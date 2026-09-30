shader_type spatial;
render_mode unshaded, cull_disabled, skip_vertex_transform;

// Falling snow for a running occasion (src/Occasions/OccasionPrecip). One mesh of a few thousand
// quads; each quad's four corners share their flake's home position (VERTEX, 0..box on every
// axis), its corner (UV, -1..1) and a per-flake hash (UV2.x). The vertex shader drops and drifts
// every flake with TIME and wraps it into a box centred on the camera — in WORLD space, so the
// flakes stay put as the camera moves through them rather than travelling with it. No CPU work
// per frame at all.

global uniform vec3 world_tint;
global uniform vec3 world_sky;

uniform float box = 60.0;
uniform float fall = 1.3;          // m/s
uniform vec2 wind = vec2(0.35, 0.15);
uniform float density = 1.0;       // 0..1: the share of flakes drawn
uniform float fog_start = 3500.0;
uniform float fog_end = 8000.0;

varying float keep;

void vertex() {
    vec3 cam = INV_VIEW_MATRIX[3].xyz;
    vec3 home = VERTEX;
    float h = UV2.x;
    vec3 drift = vec3(
        wind.x * TIME + sin(TIME * (0.6 + h) + home.y) * 0.7,
        -fall * (0.8 + 0.4 * h) * TIME,
        wind.y * TIME + cos(TIME * (0.5 + h) + home.x) * 0.7);
    vec3 corner = cam - vec3(box * 0.5);
    vec3 world = corner + mod(home + drift - corner, vec3(box));

    keep = step(h, density);
    vec3 view = (VIEW_MATRIX * vec4(world, 1.0)).xyz;
    // a flake is a couple of pixels wherever it is: grow it with distance
    float size = 0.035 + 0.0035 * length(view);
    view.xy += UV * size;
    VERTEX = view;
}

void fragment() {
    if (keep < 0.5) {
        discard;
    }
    vec3 col = vec3(0.90, 0.93, 1.0) * mix(world_tint, vec3(1.0), 0.35);
    float fog = smoothstep(fog_start, fog_end, length(VERTEX));
    ALBEDO = mix(col, world_sky, fog);
}

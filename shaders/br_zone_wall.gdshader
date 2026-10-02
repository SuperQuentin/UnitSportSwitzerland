shader_type spatial;
// The Battle Royale zone's wall (src/BattleRoyale/ZoneWall.cs): an open cylinder standing on the
// circle, scaled to it every frame. Scrolling bands and stripes, Bayer-dithered like every PS1
// surface, fading in as you come near so it never hides the far side from inside.
render_mode unshaded, cull_disabled, depth_draw_never, shadows_disabled;

uniform vec4 tint : source_color = vec4(0.62, 0.32, 1.0, 1.0);
uniform float radius = 1000.0;

varying vec3 world;

void vertex() {
    world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
    // vertical stripes about every 12 m round the circle, bands rising up it
    float around = UV.x * 6.2831853 * radius / 12.0;
    float stripe = smoothstep(0.80, 1.0, abs(sin(around)));
    float band = 0.5 + 0.5 * sin(world.y * 0.06 - TIME * 1.6);
    float near = 1.0 - smoothstep(60.0, 900.0, distance(CAMERA_POSITION_WORLD, world));
    float a = clamp(0.10 + 0.22 * band + 0.25 * stripe, 0.0, 1.0) * mix(0.55, 1.0, near);

    const float bayer[16] = float[16](0.0, 8.0, 2.0, 10.0, 12.0, 4.0, 14.0, 6.0, 3.0, 11.0, 1.0, 9.0, 15.0, 7.0, 13.0, 5.0);
    ivec2 p = ivec2(FRAGCOORD.xy) % 4;
    if (a < (bayer[p.y * 4 + p.x] + 0.5) / 16.0) discard;
    ALBEDO = tint.rgb * (0.8 + 0.4 * band + 0.3 * stripe);
}

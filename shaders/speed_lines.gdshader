// Radial speed streaks drawn over the edges of the screen, ramped by how fast the player is
// going relative to what is ordinary for their mount. A full-screen ColorRect on a CanvasLayer
// under the HUD (see PlayerFeel), like the lens layer. At intensity 0 it costs one discard per
// pixel and draws nothing.
shader_type canvas_item;

uniform float intensity : hint_range(0.0, 1.0) = 0.0;
uniform float aspect = 1.7778;
// how many angular lanes the streaks can occupy
uniform float lanes = 110.0;

float hash(float n) { return fract(sin(n * 127.1) * 43758.5453); }

void fragment() {
    if (intensity <= 0.001) discard;

    vec2 p = UV - 0.5;
    p.x *= aspect;
    float r = length(p);
    float a = atan(p.y, p.x) / 6.2831853 + 0.5;

    float lane = floor(a * lanes);
    float h = hash(lane);
    // only some lanes carry a streak, and more of them light up as the speed climbs
    float live = step(1.0 - (0.25 + 0.45 * intensity), h);
    // thin: the streak occupies the middle of its lane, not the whole wedge
    float across = abs(fract(a * lanes) - 0.5);
    float thin = 1.0 - smoothstep(0.08, 0.2, across);

    // each streak rushes outward at its own pace and length
    float along = fract(r * (1.6 + h) - TIME * (1.2 + 1.8 * h) - h * 7.0);
    float streak = smoothstep(0.0, 0.06, along) * (1.0 - smoothstep(0.18, 0.5, along));

    // kept to the rim: the centre of the picture is where the player is looking
    float rim = smoothstep(0.25, 0.62, r);
    float alpha = live * thin * streak * rim * intensity * 0.5;
    if (alpha < 0.01) discard;
    COLOR = vec4(1.0, 1.0, 1.0, alpha);
}

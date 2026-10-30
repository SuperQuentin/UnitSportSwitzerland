// A pinball backbox's display (#1325): the picture drawn on the CPU on change (PinballBackbox), shown
// unlit; the dot-matrix era masks each pixel to a round dot, the segment era a soft glow.
shader_type spatial;
render_mode unshaded, cull_back, shadows_disabled;

uniform sampler2D screen : filter_nearest, source_color;
uniform vec2 res = vec2(128.0, 32.0);
// 0 plain (reels, LCD), 1 round dots (dot-matrix), 2 glass with a faint glow (segments)
uniform int mode = 0;
uniform float bright = 1.0;

void fragment() {
	vec3 c = texture(screen, UV).rgb;
	if (mode == 1) {
		vec2 f = fract(UV * res) - 0.5;
		float d = length(f);
		c *= smoothstep(0.5, 0.32, d);
		c += vec3(0.06, 0.02, 0.0);
	} else if (mode == 2) {
		vec2 px = 1.0 / res;
		vec3 glow = texture(screen, UV + vec2(px.x, 0.0)).rgb + texture(screen, UV - vec2(px.x, 0.0)).rgb
			+ texture(screen, UV + vec2(0.0, px.y)).rgb + texture(screen, UV - vec2(0.0, px.y)).rgb;
		c = max(c, glow * 0.18);
	}
	ALBEDO = c * bright;
}

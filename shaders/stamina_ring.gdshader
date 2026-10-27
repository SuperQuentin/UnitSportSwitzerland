// The stamina ring (#1225): one ColorRect, one shader. An inner ring holds the first 100 points; with
// upgrades a thin outer ring in segments of 10 points holds the rest, so the size never jumps. Angles
// run clockwise from the top. The cost of the next climb-jump is a lighter band at the end of the fill.
// Winded = orange AND blinking (never colour alone); the panting breath is played by PlayerFeel.
// Uniforms are set only when they change (StaminaRing); the blink runs on TIME.
shader_type canvas_item;

uniform float fill : hint_range(0.0, 1.0) = 1.0;          // inner ring, share of 100 points
uniform float pending : hint_range(0.0, 1.0) = 0.0;       // inner ring, the next cost
uniform float outer_arc : hint_range(0.0, 1.0) = 0.0;     // share of the circle the outer ring spans (0 = none)
uniform float outer_fill : hint_range(0.0, 1.0) = 0.0;    // share of that arc
uniform float outer_pending : hint_range(0.0, 1.0) = 0.0;
uniform float outer_segs = 0.0;                           // 10-point segments in the outer ring
uniform float winded : hint_range(0.0, 1.0) = 0.0;
uniform vec4 accent : source_color = vec4(0.98, 0.72, 0.10, 1.0);
uniform vec4 light : source_color = vec4(1.0, 0.93, 0.66, 1.0);
uniform vec4 warn : source_color = vec4(1.0, 0.36, 0.08, 1.0);
uniform vec4 track : source_color = vec4(0.0, 0.0, 0.0, 0.55);

float band(float r, float lo, float hi) {
	float e = 0.035;
	return smoothstep(lo - e, lo + e, r) * (1.0 - smoothstep(hi - e, hi + e, r));
}

void fragment() {
	vec2 p = UV - vec2(0.5);
	float r = length(p) * 2.0;
	float a = fract(atan(p.x, -p.y) / 6.2831853);
	float blink = step(0.5, fract(TIME * 3.0));
	vec4 on = mix(accent, warn, winded);
	vec4 back = mix(track, vec4(warn.rgb * 0.55, 0.75), winded * blink);
	vec4 col = vec4(0.0);

	// inner ring
	float ring = band(r, 0.56, 0.84);
	vec4 c = back;
	if (a <= fill) {
		c = on;
		if (pending > 0.0 && a >= fill - pending) c = light;
	}
	col = mix(col, c, ring * c.a);

	// outer ring: thin, in segments, from the top
	if (outer_arc > 0.0) {
		float o = band(r, 0.90, 1.0);
		float ao = a / outer_arc;
		if (ao <= 1.0) {
			float seg = fract(ao * outer_segs);
			float gap = smoothstep(0.0, 0.12, seg) * (1.0 - smoothstep(0.88, 1.0, seg));
			vec4 oc = back;
			if (ao <= outer_fill) {
				oc = on;
				if (outer_pending > 0.0 && ao >= outer_fill - outer_pending) oc = light;
			}
			col = mix(col, oc, o * gap * oc.a);
		}
	}
	COLOR = col;
}

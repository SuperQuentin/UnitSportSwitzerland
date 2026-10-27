// The guiding gust's particles (#1226, docs/notes/adventure/guide.md): quads facing the camera, the leaf, needle and
// streak shapes drawn here. palette 0 warm leaves (orange, olive), 1 dark on snow (slate, red streaks: never white on
// white). style 0 PS1 (flat, hard edges), 1 Cartoon (an outline), 2 Realistic (soft alpha). streaks 1 at speed: no
// leaves. Uniforms are set on change only.
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never, blend_mix;

uniform int palette = 0;
uniform int style = 1;
uniform float streaks = 0.0;

varying float v_kind;
varying float v_age;
varying vec3 v_tint;

float hash(float n) { return fract(sin(n * 12.9898) * 43758.5453); }

void vertex() {
	// the particle's own turn (INSTANCE_CUSTOM.x) about the view axis, billboarded
	mat4 world = mat4(normalize(INV_VIEW_MATRIX[0]), normalize(INV_VIEW_MATRIX[1]), normalize(INV_VIEW_MATRIX[2]), MODEL_MATRIX[3]);
	float a = INSTANCE_CUSTOM.x;
	world = world * mat4(vec4(cos(a), -sin(a), 0.0, 0.0), vec4(sin(a), cos(a), 0.0, 0.0), vec4(0.0, 0.0, 1.0, 0.0), vec4(0.0, 0.0, 0.0, 1.0));
	MODELVIEW_MATRIX = VIEW_MATRIX * world;
	float h = hash(float(INSTANCE_ID) + 0.37);
	v_kind = streaks > 0.5 ? 2.0 : (h < 0.6 ? 0.0 : (h < 0.85 ? 1.0 : 2.0));
	v_age = INSTANCE_CUSTOM.y;
	float t = hash(float(INSTANCE_ID) * 1.7 + 3.1);
	vec3 warm = mix(vec3(0.93, 0.52, 0.12), vec3(0.55, 0.58, 0.18), t);
	vec3 dark = mix(vec3(0.20, 0.24, 0.30), vec3(0.62, 0.10, 0.10), step(0.7, t));
	v_tint = palette == 1 ? dark : warm;
}

void fragment() {
	vec2 p = UV * 2.0 - 1.0;
	float d;
	if (v_kind < 0.5) {
		// a leaf: an almond with a stem
		d = length(vec2(p.x * 1.7, p.y)) - (1.0 - abs(p.y)) * 0.75;
		d = min(d, max(abs(p.x) - 0.06, abs(p.y + 0.9) - 0.12));
	} else if (v_kind < 1.5) {
		// a larch needle: a thin bar
		d = max(abs(p.x) - 0.08, abs(p.y) - 0.85);
	} else {
		// a streak: long, thin, fading along
		d = max(abs(p.x) - 0.05 * (1.0 - abs(p.y)), abs(p.y) - 0.95);
	}
	float edge = style == 0 ? step(d, 0.0) : 1.0 - smoothstep(-0.05, 0.05, d);
	if (edge <= 0.01) discard;
	vec3 col = v_tint;
	if (style == 1) {
		// the cartoon outline: dark just inside the edge
		col = mix(col, vec3(0.08, 0.06, 0.05), smoothstep(-0.16, -0.05, d));
	}
	float life = smoothstep(0.0, 0.12, v_age) * (1.0 - smoothstep(0.7, 1.0, v_age));
	float alpha = style == 2 ? edge * 0.75 : edge;
	if (v_kind > 1.5) alpha *= 0.55 * (1.0 - abs(p.y));
	ALBEDO = col;
	ALPHA = alpha * life;
}

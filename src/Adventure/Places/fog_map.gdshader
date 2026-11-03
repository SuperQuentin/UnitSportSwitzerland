// The adventure map's fog of war (#1224, docs/notes/adventure/fog.md): one L8 texel per 250 m cell,
// 255 = fog, drawn over the relief with linear filtering and a soft edge. The clouds' noise drifts
// with TIME; the texture itself changes only when the server sends a FogDelta.
shader_type canvas_item;

// 0 PS1 (dithered), 1 Cartoon (flat paper clouds with a drawn outline), 2 Realistic (soft haze)
uniform int style = 1;
uniform vec2 cells = vec2(1440.0, 920.0);

float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }

float noise(vec2 p) {
	vec2 i = floor(p), f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1, 0)), u.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), u.x), u.y);
}

void fragment() {
	float f = texture(TEXTURE, UV).r;
	vec2 cell = UV * cells;
	float n = noise(cell * 0.35 + vec2(TIME * 0.05, 0.0)) * 0.66 + noise(cell * 1.1 - vec2(0.0, TIME * 0.03)) * 0.34;
	float v = f + (n - 0.5) * 0.45;
	float fog = smoothstep(0.42, 0.58, v);
	vec3 col;
	if (style == 0) {
		float d = fract(dot(floor(FRAGCOORD.xy * 0.5), vec2(0.5, 0.25)));
		fog = step(d + 0.01, fog);
		col = vec3(0.50, 0.54, 0.60) + n * 0.08;
	} else if (style == 1) {
		col = mix(vec3(0.90, 0.89, 0.85), vec3(0.99, 0.98, 0.96), n);
		float outline = smoothstep(0.40, 0.46, v) - smoothstep(0.46, 0.52, v);
		col = mix(col, vec3(0.38, 0.40, 0.46), clamp(outline, 0.0, 1.0) * 0.85);
		fog = max(fog, outline);
	} else {
		col = vec3(0.80, 0.83, 0.87) + (n - 0.5) * 0.06;
		fog *= 0.9;
	}
	COLOR = vec4(col, fog * 0.95);
}

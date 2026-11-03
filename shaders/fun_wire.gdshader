// Wireframe neon (#1396, World/Fun/FunBackdrop's mountains): a dark fill with every triangle's edges glowing. Each
// triangle's three corners carry (1,0,0), (0,1,0), (0,0,1) in their vertex colour, so the smallest component is the
// distance to an edge; fwidth keeps the lines a pixel or two wide at any range. Brighter toward the peaks.
shader_type spatial;
render_mode unshaded, cull_disabled, shadows_disabled, fog_disabled;

uniform vec3 fill : source_color = vec3(0.06, 0.01, 0.12);
uniform vec3 edge : source_color = vec3(0.15, 0.9, 1.0);
uniform vec3 edge_high : source_color = vec3(1.0, 0.25, 0.9);
uniform float energy = 1.7;
uniform float peak = 1500.0;
uniform float base_height = 500.0;

varying float height;

void vertex() {
	height = (MODEL_MATRIX * vec4(VERTEX, 1.0)).y - base_height;
}

void fragment() {
	vec3 b = COLOR.rgb;
	vec3 w = fwidth(b);
	vec3 e = smoothstep(vec3(0.0), w * 1.4, b);
	float line = 1.0 - min(min(e.x, e.y), e.z);
	vec3 lc = mix(edge, edge_high, clamp(height / peak, 0.0, 1.0)) * energy;
	ALBEDO = mix(fill, lc, line);
}

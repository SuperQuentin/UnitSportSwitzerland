// The shared pot's cheese surface (#1266, Adventure/Together/FondueTable): a disc in the caquelon. `bubble` (0..1)
// is how hard it bubbles (the heat and the split meter), `split` (0..1) greys and curdles it when the cheese
// splits. Both are set by the table only when they change; the bubbling runs on TIME.
shader_type spatial;

uniform float split : hint_range(0.0, 1.0) = 0.0;
uniform float bubble : hint_range(0.0, 1.0) = 0.3;
uniform vec4 cheese : source_color = vec4(0.98, 0.84, 0.36, 1.0);
uniform vec4 curdled : source_color = vec4(0.74, 0.70, 0.56, 1.0);

float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }

void vertex() {
	// slow swirls, faster with the heat
	float r = length(VERTEX.xz);
	VERTEX.y += sin(r * 40.0 - TIME * (1.0 + 3.0 * bubble)) * 0.002 * (1.0 - r * 6.0);
}

void fragment() {
	vec2 uv = UV * 18.0;
	vec2 cell = floor(uv);
	vec2 f = fract(uv) - 0.5;
	// a bubble per cell, each on its own beat
	float beat = fract(TIME * (0.4 + bubble * 1.6) + hash(cell));
	float b = smoothstep(0.25 * beat, 0.0, length(f)) * step(0.55, hash(cell + 3.1)) * bubble;
	vec3 col = mix(cheese.rgb, curdled.rgb, split);
	// curds: a grain of darker spots once it splits
	col -= vec3(0.12) * split * step(0.6, hash(floor(UV * 60.0)));
	col += vec3(0.12) * b;
	ALBEDO = col;
	ROUGHNESS = mix(0.35, 0.9, split);
	SPECULAR = 0.6 * (1.0 - split);
}

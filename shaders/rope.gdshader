// A climbing rope between two harnesses (#1266, Adventure/Together/RopeNode): one shared unit tube (8 sides,
// 12 rings, y 0..1) stretched between the two points by its node's transform each frame. The sag is here:
// a parabola hanging straight down in world space, `rope_sag` metres at the middle (0 taut), set per
// instance only when it changes by more than 5 cm. A twisted stripe runs along it. No mesh is ever rebuilt.
shader_type spatial;
render_mode cull_disabled;

instance uniform float rope_sag = 0.0;
uniform vec4 rope_color : source_color = vec4(0.86, 0.20, 0.12, 1.0);
uniform vec4 stripe_color : source_color = vec4(0.98, 0.84, 0.22, 1.0);

varying float along;
varying float around;

void vertex() {
	float t = clamp(VERTEX.y, 0.0, 1.0);
	along = t;
	around = atan(VERTEX.z, VERTEX.x);
	// world down in this node's (stretched) frame: the rope hangs, whichever way it runs
	vec3 down_local = (inverse(MODEL_MATRIX) * vec4(0.0, -1.0, 0.0, 0.0)).xyz;
	VERTEX += down_local * rope_sag * 4.0 * t * (1.0 - t);
}

void fragment() {
	// a kernmantle's twisted fleck: a helix of the stripe colour
	float helix = fract(along * 60.0 + around / 6.2831853 * 2.0);
	float fleck = step(0.72, helix);
	ALBEDO = mix(rope_color.rgb, stripe_color.rgb, fleck);
	ROUGHNESS = 0.85;
}

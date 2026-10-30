// The flag swinger's cloth (#1269 part 5, Occasions/Festivals/Fahnen/FahnenFlag): a flat grid, pinned along its staff edge (UV.x = 0),
// that ripples with TIME. `swing` (0..1) is how fast the flag is being swung: the ripple grows and the cloth streams behind the staff.
// The Swiss cross is painted here, so the mesh is built once and never rebuilt.
shader_type spatial;
render_mode cull_disabled, diffuse_lambert;

uniform float swing : hint_range(0.0, 1.0) = 0.0;
uniform vec4 field : source_color = vec4(0.84, 0.09, 0.12, 1.0);
uniform vec4 cross_colour : source_color = vec4(0.97, 0.97, 0.95, 1.0);

void vertex() {
	float free_edge = UV.x;   // 0 at the staff, 1 at the free edge
	float amp = (0.025 + 0.14 * swing) * free_edge;
	float wave = sin(UV.x * 7.0 - TIME * (4.0 + 9.0 * swing) + UV.y * 2.0);
	VERTEX.z += wave * amp;
	// swung hard, the cloth streams out behind the staff and droops less
	VERTEX.y -= (1.0 - swing) * 0.05 * free_edge * free_edge;
	VERTEX.x -= swing * 0.12 * free_edge * free_edge;
	NORMAL = normalize(vec3(-wave * amp * 4.0, 0.0, 1.0));
}

void fragment() {
	vec2 c = abs(UV - vec2(0.5));
	// a cross whose arms are 1/6 wide and 5/9 long, the proportions of the Swiss flag
	bool arm_h = c.y < 1.0 / 12.0 && c.x < 5.0 / 18.0;
	bool arm_v = c.x < 1.0 / 12.0 && c.y < 5.0 / 18.0;
	ALBEDO = (arm_h || arm_v) ? cross_colour.rgb : field.rgb;
	ROUGHNESS = 0.9;
}

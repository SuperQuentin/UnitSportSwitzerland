// The Böögg pyre's flames and smoke (#1358, docs/notes/occasions/festival-sechselaeuten.md): billboard quads in one MultiMesh each, animated
// entirely here from TIME and one `ramp` uniform (nothing is rebuilt or moved from the CPU). Each instance is a puff that is born at its own
// spot, rises for `rise` metres over its life, swells and fades; INSTANCE_CUSTOM is (phase offset 0..1, size in m, speed jitter 0..1, unused).
// `smoke_mix` 0 is fire (bright, nearly additive), 1 is smoke (dark, over-blended). Nothing here knows how long the burn will last.
shader_type spatial;
render_mode unshaded, blend_premul_alpha, cull_disabled, depth_draw_never, fog_disabled;

uniform float ramp : hint_range(0.0, 1.0) = 0.0;
uniform float smoke_mix : hint_range(0.0, 1.0) = 0.0;
uniform float rise = 4.0;
uniform float speed = 0.8;

varying float v_life;

void vertex() {
	float life = fract(TIME * speed * (0.7 + INSTANCE_CUSTOM.z * 0.6) + INSTANCE_CUSTOM.x);
	v_life = life;
	vec3 base = (MODEL_MATRIX * vec4(0.0, 0.0, 0.0, 1.0)).xyz;
	base.y += life * rise * (0.4 + 0.6 * ramp);
	// fire is fat in the middle of its life and tapers to a tip; smoke keeps swelling
	float swell = mix(sin(life * 3.14159) * (1.0 - 0.3 * life), 0.5 + life, smoke_mix);
	float size = INSTANCE_CUSTOM.y * ramp * swell;
	vec3 right = INV_VIEW_MATRIX[0].xyz;
	vec3 up = INV_VIEW_MATRIX[1].xyz;
	vec3 world = base + (right * VERTEX.x + up * VERTEX.y) * size;
	POSITION = PROJECTION_MATRIX * VIEW_MATRIX * vec4(world, 1.0);
}

void fragment() {
	float d = length(UV - vec2(0.5)) * 2.0;
	float soft = smoothstep(1.0, 0.15, d);
	vec3 fire_col = mix(vec3(1.0, 0.92, 0.5), vec3(1.0, 0.3, 0.04), v_life);
	float fire_a = soft * (1.0 - v_life);
	float smoke_a = soft * sin(v_life * 3.14159) * 0.5;
	vec3 smoke_col = vec3(0.1, 0.1, 0.11);
	vec3 col = mix(fire_col * fire_a * 1.5, smoke_col * smoke_a, smoke_mix);
	float alpha = mix(fire_a * 0.2, smoke_a, smoke_mix);
	ALBEDO = col;
	ALPHA = alpha;
}

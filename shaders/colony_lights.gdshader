// The Moon colonies' lights from afar (#1946, src/World/Moon/ColonyLights.cs): each light is a quad whose four corners
// share its point; the vertex shader lays it a few pixels across on screen whatever the distance, pulled a little toward
// the camera so the globe's coarse ground never hides it. Only where the sun is down at the light (its up against the
// sun), brighter nearer, gone within 10 km where the colony itself is drawn.
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled, shadows_disabled, fog_disabled, skip_vertex_transform;

// world axes, toward the Sun from the Moon
uniform vec3 sun_dir = vec3(0.0, 1.0, 0.0);
// a light's size on screen, pixels across
uniform float size_px = 3.0;

varying vec2 corner;
varying float strength;

void vertex() {
	vec4 v = MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
	// the root shrinks the Moon about the camera when it is far: true metres are view metres over that scale
	float scale = length(MODEL_MATRIX[0].xyz);
	float metres = length(v.xyz) / max(scale, 1e-9);
	vec3 up = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
	float night = smoothstep(0.04, -0.06, dot(up, normalize(sun_dir)));
	float near = smoothstep(6000.0, 14000.0, metres);
	// brighter nearer: full under 300 km, a third at the Earth's distance
	float far = clamp(300000.0 / max(metres, 1.0), 0.33, 1.0);
	strength = COLOR.a * night * near * far;
	// a little toward the camera: 0.4 % of the way (400 m at 100 km), over the globe's coarse cells
	v.xyz *= 0.996;
	VERTEX = v.xyz;
	POSITION = PROJECTION_MATRIX * v;
	vec2 px = UV * size_px * (0.6 + 0.4 * COLOR.a);
	POSITION.xy += px / VIEWPORT_SIZE * POSITION.w;
	corner = UV;
	if (strength <= 0.001) POSITION = vec4(2.0, 2.0, 2.0, 1.0);
}

void fragment() {
	float d = length(corner);
	float a = clamp(1.0 - d, 0.0, 1.0);
	ALBEDO = COLOR.rgb * strength * (a * a * 2.5);
}

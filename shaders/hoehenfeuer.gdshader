// The 1 August's Höhenfeuer (#1355): one MultiMesh of flame billboards on the summits, visible from 15 km.
// Each instance is a quad standing on its summit (the transform's scale is its width and height in metres), turned about the vertical axis to face the
// camera, grown with distance so a fire far off is still a few pixels, and flickering on the GPU from a per-instance phase (INSTANCE_CUSTOM.x): nothing is
// rebuilt per frame (docs/notes/general/no-mesh-rebuild-per-frame.md).
shader_type spatial;
render_mode unshaded, blend_add, cull_disabled, depth_draw_never, fog_disabled, shadows_disabled;

uniform vec3 core_color : source_color = vec3(1.0, 0.86, 0.38);
uniform vec3 edge_color : source_color = vec3(1.0, 0.30, 0.04);
uniform float pixel_angle = 0.004; // the smallest size a flame is drawn at, radians of the view

varying float v_phase;

void vertex() {
	v_phase = INSTANCE_CUSTOM.x;
	vec3 centre = MODEL_MATRIX[3].xyz;
	float width = length(MODEL_MATRIX[0].xyz);
	float height = length(MODEL_MATRIX[1].xyz);
	float dist = length((VIEW_MATRIX * vec4(centre, 1.0)).xyz);
	float grow = max(1.0, pixel_angle * dist / max(width, 0.01));
	float flick = 0.90 + 0.10 * sin(TIME * 9.0 + v_phase * 40.0) + 0.06 * sin(TIME * 23.0 + v_phase * 91.0);
	vec3 right = normalize(vec3(INV_VIEW_MATRIX[0].x, 0.0, INV_VIEW_MATRIX[0].z));
	vec3 pos = centre + right * (VERTEX.x * width * grow) + vec3(0.0, VERTEX.y * height * grow * flick, 0.0);
	POSITION = PROJECTION_MATRIX * VIEW_MATRIX * vec4(pos, 1.0);
}

void fragment() {
	float h = 1.0 - UV.y;                       // 0 at the logs, 1 at the tip
	float x = (UV.x - 0.5) * 2.0;
	float sway = sin(h * 9.0 - TIME * 7.0 + v_phase * 30.0) * 0.10 * h;
	float width = pow(max(1.0 - h, 0.0), 0.8) * smoothstep(0.0, 0.12, h + 0.04);
	float d = abs(x - sway) / max(width, 0.001);
	float body = smoothstep(1.0, 0.25, d) * (1.0 - h * h);
	ALBEDO = mix(core_color, edge_color, clamp(h * 1.3, 0.0, 1.0)) * body * 1.8;
}

// A cinema projector's beam (#1238, Interiors/InteriorNode.Cinema): the dust in the light between the lens
// and the screen, in the picture's colours (#1250). Drawn on the cone's far faces only (one fragment a
// pixel, inside the cone too): the view ray is marched back from there toward the camera, each sample
// projected from the lens onto the screen and the picture read there, blurred (a high mip: the beam is
// haze, not a second image). So looking back at the lens from the seats the colours converge on it as the
// picture's do, and from the side each part of the beam carries the part of the picture it lights.
// All in the interior node's space: apex, the screen's centre and half extents are per-beam instance
// uniforms (InteriorNode.AddScreens); the picture is the shared material's (InteriorNode.ShowSlide).
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_front, shadows_disabled;

uniform sampler2D picture : source_color, filter_linear_mipmap, repeat_disable;
uniform float density = 0.045;
uniform float haze_mip = 3.5;

instance uniform vec3 apex;
instance uniform vec3 centre;
instance uniform vec3 half_right;
instance uniform vec3 half_up;

varying vec3 local_pos;
varying vec3 local_cam;

void vertex() {
	local_pos = VERTEX;
	local_cam = (inverse(MODEL_MATRIX) * vec4(CAMERA_POSITION_WORLD, 1.0)).xyz;
}

// the picture's colour lighting a point of the cone, and how far along the throw it is (0 lens, 1 screen); a
// point outside the cone gives nothing
vec4 lit_by(vec3 q, vec3 n, float plane) {
	vec3 d = q - apex;
	float along = dot(d, n);
	if (along <= 1e-4) {
		return vec4(0.0);
	}
	float t = plane / along;
	if (t < 1.0) {
		return vec4(0.0);
	}
	vec3 hit = apex + d * t - centre;
	float u = dot(hit, half_right) / dot(half_right, half_right);
	float v = dot(hit, half_up) / dot(half_up, half_up);
	if (abs(u) > 1.0 || abs(v) > 1.0) {
		return vec4(0.0);
	}
	vec3 c = textureLod(picture, vec2(u * 0.5 + 0.5, 0.5 - v * 0.5), haze_mip).rgb;
	return vec4(c, 1.0 / t);
}

void fragment() {
	vec3 n = normalize(cross(half_right, half_up));
	if (dot(centre - apex, n) < 0.0) {
		n = -n;
	}
	float plane = dot(centre - apex, n);
	vec3 to_cam = local_cam - local_pos;
	float len = length(to_cam);
	vec3 dir = to_cam / max(len, 1e-4);
	// as far back as the camera, never past the throw's length
	float reach = min(len, length(centre - apex) * 1.2);
	const int STEPS = 14;
	vec3 sum = vec3(0.0);
	for (int i = 0; i < STEPS; i++) {
		vec3 q = local_pos + dir * reach * (float(i) + 0.5) / float(STEPS);
		vec4 s = lit_by(q, n, plane);
		// brighter at the lens, where the light is packed tight
		sum += s.rgb * mix(1.0, 0.25, s.a);
	}
	ALBEDO = sum * density * reach / float(STEPS);
}

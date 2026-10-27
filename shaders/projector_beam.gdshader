// A cinema projector's beam (#1238, Interiors/InteriorNode.Cinema): the dust in the light between the lens
// and the screen, in the picture's colours (#1250). Drawn on the cone's far faces only (one fragment a
// pixel, inside the cone too; a face is far when the cone lies between it and the camera, whatever its
// winding). #1279, after the owner's "weird from the side, too strong, no shadow":
// - the view ray is clipped to the pyramid (Cyrus-Beck: four sides, the lens's plane, the screen) and the light
//   scattered toward the eye summed along it with equiangular samples round the lens (Kulla & Fajardo 2012):
//   the light per metre falls as 1/d^2 from the lens, so the beam is bright at the projector and fades to
//   nearly nothing at the screen, and the few samples sit where the light is;
// - a Henyey-Greenstein phase (dust scatters forward) with a little isotropic floor: bright looking back into
//   the lens from the seats, faint from the side, as a real beam;
// - the people in it shadow it: each sample's line to the lens tested against up to eight upright capsules
//   (body[i]: node-local x, z of the feet, their y, the head's y), set each frame for the hall being projected.
// All in the interior node's space: apex, the screen's centre and half extents are per-beam material
// uniforms (InteriorNode.AddScreens), the picture set on every beam at each slide (InteriorNode.ShowSlide).
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled, shadows_disabled;

uniform sampler2D picture : source_color, filter_linear_mipmap, repeat_disable;
uniform float density = 0.35;
uniform float haze_mip = 3.5;
// forward scattering of the dust, and the share of light scattered evenly
uniform float g = 0.6;
uniform float isotropic = 0.2;

uniform vec3 apex;
uniform vec3 centre;
uniform vec3 half_right;
uniform vec3 half_up;
// the bodies in the beam (w <= z: none)
uniform vec4 body0 = vec4(0.0, 0.0, 0.0, -1.0);
uniform vec4 body1 = vec4(0.0, 0.0, 0.0, -1.0);
uniform vec4 body2 = vec4(0.0, 0.0, 0.0, -1.0);
uniform vec4 body3 = vec4(0.0, 0.0, 0.0, -1.0);
uniform vec4 body4 = vec4(0.0, 0.0, 0.0, -1.0);
uniform vec4 body5 = vec4(0.0, 0.0, 0.0, -1.0);
uniform vec4 body6 = vec4(0.0, 0.0, 0.0, -1.0);
uniform vec4 body7 = vec4(0.0, 0.0, 0.0, -1.0);
const float BODY_RADIUS = 0.22;
const int STEPS = 24;

varying vec3 local_pos;
varying vec3 local_cam;

void vertex() {
	local_pos = VERTEX;
	local_cam = (inverse(MODEL_MATRIX) * vec4(CAMERA_POSITION_WORLD, 1.0)).xyz;
}

// the picture's colour lighting a point of the cone; a point outside the cone gives nothing (alpha 0)
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
	return vec4(textureLod(picture, vec2(u * 0.5 + 0.5, 0.5 - v * 0.5), haze_mip).rgb, 1.0);
}

// whether a body stands between the lens and q: the segment's nearest point to the capsule's axis, across
bool blocked_by(vec4 b, vec3 q) {
	if (b.w <= b.z) {
		return false;
	}
	vec3 s = q - apex;
	vec2 sxz = s.xz;
	float k = clamp(dot(b.xy - apex.xz, sxz) / max(dot(sxz, sxz), 1e-6), 0.0, 1.0);
	vec3 p = apex + s * k;
	return k < 0.999 && distance(p.xz, b.xy) < BODY_RADIUS && p.y > b.z && p.y < b.w;
}

bool shadowed(vec3 q) {
	return blocked_by(body0, q) || blocked_by(body1, q) || blocked_by(body2, q) || blocked_by(body3, q)
		|| blocked_by(body4, q) || blocked_by(body5, q) || blocked_by(body6, q) || blocked_by(body7, q);
}

// clips the ray o + t*v, t in [ta, tb], to the half-space dot(m, q - p) >= 0
void clip(vec3 m, vec3 p, vec3 o, vec3 v, inout float ta, inout float tb) {
	float num = dot(m, o - p);
	float den = dot(m, v);
	if (abs(den) < 1e-6) {
		if (num < 0.0) {
			tb = -1.0;
		}
		return;
	}
	float t = -num / den;
	if (den > 0.0) {
		ta = max(ta, t);
	} else {
		tb = min(tb, t);
	}
}

void fragment() {
	vec3 n = normalize(cross(half_right, half_up));
	if (dot(centre - apex, n) < 0.0) {
		n = -n;
	}
	float plane = dot(centre - apex, n);
	vec3 to_frag = local_pos - local_cam;
	float len = length(to_frag);
	vec3 v = to_frag / max(len, 1e-4);
	// the near faces give way to the far ones: just toward the camera from here must be inside the cone
	if (lit_by(local_pos - v * 0.03, n, plane).a <= 0.0) {
		discard;
	}
	// the stretch of the view ray inside the pyramid, from the camera (or where it enters) to this far face
	float ta = 0.0, tb = len;
	vec3 c[4] = vec3[](centre - half_right - half_up, centre + half_right - half_up, centre + half_right + half_up, centre - half_right + half_up);
	vec3 inward = centre - apex;
	for (int i = 0; i < 4; i++) {
		vec3 m = cross(c[i] - apex, c[(i + 1) % 4] - apex);
		if (dot(m, inward) < 0.0) {
			m = -m;
		}
		clip(m, apex, local_cam, v, ta, tb);
	}
	clip(n, apex, local_cam, v, ta, tb);
	clip(-n, centre, local_cam, v, ta, tb);
	if (tb <= ta) {
		discard;
	}
	// equiangular samples round the lens: t = t0 + h tan(theta), each weighing (theta_b - theta_a) / (h N)
	float t0 = dot(apex - local_cam, v);
	float h = max(length(local_cam + v * t0 - apex), 0.05);
	float th_a = atan((ta - t0) / h), th_b = atan((tb - t0) / h);
	// a fixed jitter per pixel, so the steps do not band
	float jitter = fract(52.9829189 * fract(dot(FRAGCOORD.xy, vec2(0.06711056, 0.00583715))));
	vec3 sum = vec3(0.0);
	for (int i = 0; i < STEPS; i++) {
		float th = mix(th_a, th_b, (float(i) + jitter) / float(STEPS));
		vec3 q = local_cam + v * (t0 + h * tan(th));
		vec4 s = lit_by(q, n, plane);
		if (s.a <= 0.0 || shadowed(q)) {
			continue;
		}
		// toward the eye from the light's way: forward scattering bright, the side faint
		float cos_t = dot(normalize(q - apex), -v);
		float hg = (1.0 - g * g) / (12.566 * pow(1.0 + g * g - 2.0 * g * cos_t, 1.5));
		sum += s.rgb * mix(hg, 0.0796, isotropic);
	}
	vec3 light = sum * (th_b - th_a) / (h * float(STEPS));
	ALBEDO = min(light * density, vec3(1.2));
}

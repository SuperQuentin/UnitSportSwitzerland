// VR loading space (#1101): what the headset shows round the loading panel while the world is still
// streaming in. A sphere centred on the head, shaded by the world direction so it stays put as the
// head turns: a still night-blue sky, a pale band at the horizon and a dark floor with a few soft
// rings under the feet. Nothing moves and nothing is fine, so the Air Link encoder has almost nothing
// to send, and the eyes have a horizon and a floor to stand on.
shader_type spatial;
render_mode unshaded, cull_front, depth_draw_never, shadows_disabled, fog_disabled;

// how far the floor is below the eyes, m (the calibrated head height)
uniform float eye_height = 1.6;

const vec3 TOP = vec3(0.035, 0.05, 0.10);
const vec3 HORIZON = vec3(0.16, 0.22, 0.33);
const vec3 FLOOR = vec3(0.05, 0.06, 0.08);
const vec3 RING = vec3(0.22, 0.30, 0.44);

varying vec3 dir;

void vertex() {
	dir = (MODEL_MATRIX * vec4(VERTEX, 0.0)).xyz;
}

void fragment() {
	vec3 d = normalize(dir);
	vec3 c;
	if (d.y >= 0.0) {
		c = mix(HORIZON, TOP, pow(d.y, 0.45));
	} else {
		// where the view meets a floor eye_height below: rings 2 m apart, wide and soft, gone by 14 m
		float r = length(d.xz) * eye_height / max(-d.y, 0.02);
		float ring = 1.0 - smoothstep(0.0, 0.35, abs(fract(r * 0.5 + 0.5) - 0.5) * 2.0);
		float fade = 1.0 - smoothstep(4.0, 14.0, r);
		c = mix(FLOOR, RING, ring * fade * 0.6);
		// the floor melts into the horizon band in the distance, so there is no hard edge to crawl
		c = mix(c, HORIZON * 0.7, smoothstep(-0.12, 0.0, d.y));
	}
	ALBEDO = c;
}

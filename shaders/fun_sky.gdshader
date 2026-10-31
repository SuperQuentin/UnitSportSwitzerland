// The fun world's synthwave sky (#1396, World/Fun/FunBackdrop): a violet dome deepening to night overhead with a few
// stars, a hot pink band along the horizon, and a big striped retro sun sitting on it where the real sun is (sun_dir),
// its lower half cut by bands that widen toward the horizon. Brighter than white in the sun so the glow haloes it.
// Nothing is set per frame.
shader_type sky;

uniform vec3 zenith : source_color = vec3(0.03, 0.01, 0.09);
uniform vec3 upper : source_color = vec3(0.17, 0.04, 0.32);
uniform vec3 horizon : source_color = vec3(0.98, 0.28, 0.55);
uniform vec3 below : source_color = vec3(0.09, 0.02, 0.16);
uniform vec3 sun_top : source_color = vec3(1.0, 0.86, 0.25);
uniform vec3 sun_bottom : source_color = vec3(1.0, 0.16, 0.58);
uniform vec3 sun_dir = vec3(-0.99, 0.11, 0.08);
uniform float sun_radius = 0.2;
uniform float sun_energy = 2.2;

float hash(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

void sky() {
	vec3 d = normalize(EYEDIR);
	float h = d.y;
	vec3 c;
	if (h >= 0.0) {
		c = mix(horizon, upper, smoothstep(0.0, 0.22, h));
		c = mix(c, zenith, smoothstep(0.22, 0.75, h));
		// stars above the haze: one in a few hundred cells, twinkling slowly
		vec2 cell = floor(vec2(atan(d.x, d.z) * 160.0, h * 160.0));
		float star = step(0.9965, hash(cell)) * smoothstep(0.18, 0.45, h);
		c += vec3(0.9, 0.85, 1.0) * star * (0.6 + 0.4 * sin(TIME * 1.3 + hash(cell + 7.0) * 40.0));
	} else {
		c = mix(horizon * 0.5, below, smoothstep(0.0, 0.08, -h));
	}

	// the retro sun: a disc on the sun's direction, its centre a little above the horizon
	vec3 s = normalize(sun_dir);
	vec3 right = normalize(cross(vec3(0.0, 1.0, 0.0), s));
	vec3 up = cross(s, right);
	float facing = dot(d, s);
	if (facing > 0.0) {
		vec2 uv = vec2(dot(d, right), dot(d, up)) / facing / sun_radius;
		float r = length(uv);
		// the bands across its lower half: thin near the middle, thick near the bottom
		float v = -uv.y;
		float band = v > 0.0 ? step(fract(v * 4.5), smoothstep(0.0, 1.0, v) * 0.75) : 0.0;
		float disc = (1.0 - smoothstep(0.985, 1.0, r)) * (1.0 - band);
		vec3 sun = mix(sun_bottom, sun_top, clamp(uv.y * 0.5 + 0.5, 0.0, 1.0)) * sun_energy;
		c = mix(c, sun, disc);
		// a soft glow round it
		c += sun_bottom * 0.35 * exp(-max(r - 1.0, 0.0) * 2.5) * (1.0 - disc);
	}
	COLOR = c;
}

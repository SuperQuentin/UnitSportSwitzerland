// The air round the globe (#1430 H4, World/Globe/Atmosphere.cs): single scattering, Rayleigh and
// Mie, marched along each pixel's ray through a shell 100 km thick, up to the first thing the
// scene drew there (the depth buffer: the globe, the flat tiles) or the ground. From outside it is
// the blue limb and the haze over the Earth; from inside, high up, the dark sky above. Drawn on a
// sphere's back faces (once a pixel, inside or out), pulled in short of the far plane. Premultiplied:
// what lies behind is dimmed by the air's transmittance along the ray, and the scattered light added.
shader_type spatial;
render_mode unshaded, blend_premul_alpha, depth_draw_never, depth_test_disabled, cull_front, fog_disabled, shadows_disabled;

uniform vec3 planet_centre;
uniform float planet_r = 6371000.0;
uniform float atmo_r = 6471000.0;
// toward the sun, world axes
uniform vec3 sun_dir = vec3(0.0, 1.0, 0.0);
uniform float intensity = 1.0;
uniform float sun_power = 12.0;
// the shell's vertices are pulled in to this distance, short of the far plane
uniform float clamp_dist = 1000000.0;
uniform sampler2D depth_tex : hint_depth_texture, filter_nearest;

const vec3 BETA_R = vec3(5.8e-6, 13.5e-6, 33.1e-6);
// a clear day's aerosols (21e-6 is a hazy one: it greyed the whole Earth from orbit)
const float BETA_M = 4e-6;
const float H_R = 8000.0;
const float H_M = 1200.0;
const float G = 0.76;
const int STEPS = 16;
const int LIGHT_STEPS = 4;

void vertex() {
	vec4 view = MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
	float d = length(view.xyz);
	if (d > clamp_dist) view.xyz *= clamp_dist / d;
	POSITION = PROJECTION_MATRIX * view;
}

// the ray's entry and exit of a sphere round the origin, or (-1, -1) when it misses (both behind)
vec2 sphere(vec3 o, vec3 d, float r) {
	float b = dot(o, d);
	float c = dot(o, o) - r * r;
	float disc = b * b - c;
	if (disc < 0.0) return vec2(-1.0, -1.0);
	float s = sqrt(disc);
	return vec2(-b - s, -b + s);
}

void fragment() {
	float depth = texture(depth_tex, SCREEN_UV).x;
	vec4 at = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, max(depth, 1e-7), 1.0);
	at.xyz /= at.w;
	vec3 dir = normalize((INV_VIEW_MATRIX * vec4(at.xyz, 0.0)).xyz);
	// reversed Z: 0 is the far plane, nothing drawn
	float scene = depth > 1e-7 ? length(at.xyz) : 1e12;

	vec3 o = CAMERA_POSITION_WORLD - planet_centre;
	vec2 air = sphere(o, dir, atmo_r);
	if (air.y <= 0.0) discard;
	float t0 = max(air.x, 0.0), t1 = air.y;
	vec2 ground = sphere(o, dir, planet_r);
	if (ground.x > 0.0) t1 = min(t1, ground.x);
	t1 = min(t1, scene);
	if (t1 <= t0) discard;

	float mu = dot(dir, sun_dir);
	float phase_r = 3.0 / (16.0 * PI) * (1.0 + mu * mu);
	float g2 = G * G;
	float phase_m = 3.0 / (8.0 * PI) * ((1.0 - g2) * (1.0 + mu * mu)) / ((2.0 + g2) * pow(1.0 + g2 - 2.0 * G * mu, 1.5));

	float ds = (t1 - t0) / float(STEPS);
	float od_r = 0.0, od_m = 0.0;
	vec3 sum_r = vec3(0.0), sum_m = vec3(0.0);
	for (int i = 0; i < STEPS; i++) {
		vec3 p = o + dir * (t0 + (float(i) + 0.5) * ds);
		float h = length(p) - planet_r;
		float dr = exp(-h / H_R) * ds, dm = exp(-h / H_M) * ds;
		od_r += dr;
		od_m += dm;
		// the light's way in from the sun: none when the ground is in the way
		vec2 out_air = sphere(p, sun_dir, atmo_r);
		vec2 shade = sphere(p, sun_dir, planet_r);
		if (shade.x > 0.0) continue;
		float ls = out_air.y / float(LIGHT_STEPS), lr = 0.0, lm = 0.0;
		for (int j = 0; j < LIGHT_STEPS; j++) {
			float lh = length(p + sun_dir * ((float(j) + 0.5) * ls)) - planet_r;
			lr += exp(-lh / H_R) * ls;
			lm += exp(-lh / H_M) * ls;
		}
		vec3 tau = BETA_R * (od_r + lr) + BETA_M * 1.1 * (od_m + lm);
		vec3 att = exp(-tau);
		sum_r += dr * att;
		sum_m += dm * att;
	}
	{
		vec3 through = exp(-(BETA_R * od_r + BETA_M * 1.1 * od_m));
		ALBEDO = intensity * sun_power * (sum_r * BETA_R * phase_r + sum_m * BETA_M * phase_m);
		ALPHA = intensity * (1.0 - dot(through, vec3(0.3333)));
	}
}

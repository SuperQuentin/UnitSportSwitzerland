// The stones on another body's ground (#2016, World/Bodies/BodyScatter.cs: rocks, pebbles, ice blocks, cobbles; and the
// boulders round fresh craters, BodyBoulders), lit as the ground they lie on (body_ground.gdshader): the scene's
// directional light alone at the ground's strength and tint (BodyGround.Light), its shadows, no ambient (no air to fill
// the shadow side: the Moon's black side of a stone), the night's floor as emission. Each instance's colour is the
// MultiMesh's (its tint by its seed).
shader_type spatial;
render_mode cull_back, fog_disabled, ambient_light_disabled, specular_disabled;

uniform float light_strength = 0.9;
uniform vec3 light_tint = vec3(1.0);
uniform float night_floor = 0.012;
uniform vec3 air_tau = vec3(0.0);
// the regolith's own law on a dusty stone (as the ground's lunar); ice and a body with air 0
uniform float lunar = 0.0;
// fresh ice's glint at a low sun seen against it (Europa's blocks); 0 for rock
uniform float sheen = 0.0;
// the sky's light on a body with air (Mars' dusty sky fills a stone's shadow side), a share of the direct light; 0 airless
uniform float sky_fill = 0.0;

varying vec3 tint;

void vertex() {
	tint = COLOR.rgb;
}

void fragment() {
	ALBEDO = tint;
	EMISSION = tint * (night_floor + sky_fill * light_strength * light_tint * (0.6 + 0.4 * NORMAL.y));
}

void light() {
	if (LIGHT_IS_DIRECTIONAL) {
		float l = max(dot(NORMAL, LIGHT), 0.0);
		float e = max(dot(NORMAL, VIEW), 0.05);
		float shade = mix(l, 2.0 * l / (l + e), lunar);
		vec3 through = exp(-(air_tau - vec3(min(min(air_tau.r, air_tau.g), air_tau.b))) / max(l, 0.05));
		vec3 h = normalize(LIGHT + VIEW);
		float glint = sheen * pow(max(dot(NORMAL, h), 0.0), 24.0) * l;
		DIFFUSE_LIGHT += light_tint * light_strength * (shade + glint) * ATTENUATION * through;
	}
}

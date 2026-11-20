// The ground under the camera on another body (#2024, World/Bodies/BodyGround.cs: its near and medium squares): the
// globe's colour and lift (shaders/body_globe.gdshader), but lit by the scene's sun light so it takes shadows: the
// player's, a boulder's, a colony's and its own crater rims'. No air, so no ambient: the light that is not the sun is
// the night's floor and the colony's lamps, added as emission. The scene's light is the sun by day and the Earth at
// night (World/DayNight: earthshine from where the Earth hangs); light_strength and light_tint say how strong and what
// colour it is here (BodyGround), its own colour and energy (the style's palette) are left out.
shader_type spatial;
render_mode cull_disabled, fog_disabled, ambient_light_disabled, specular_disabled;

uniform sampler2D albedo_tex : source_color, filter_linear_mipmap, repeat_disable;
// the scene's directional light here: the exposed sun by day, the earthshine at night (BodyGround.Light)
uniform float light_strength = 0.9;
uniform vec3 light_tint = vec3(1.0);
// what the ground gets with no light on it: starlight, the scattered light of the lit ground round a shadow
uniform float night_floor = 0.012;
// the air's vertical optical depth per channel (Mars, Venus, Titan; zero without air), as body_globe.gdshader
uniform vec3 air_tau = vec3(0.0);
// how much of the light follows the regolith's own law (#2024): Lommel-Seeliger, 2 cos i / (cos i + cos e), bright to a
// grazing sun seen obliquely, flat under a high one (the full Moon has no limb darkening); the rest plain Lambert. A body
// with air (Mars) 0
uniform float lunar = 0.0;
// Cartoon (the default style): the photo's albedo lifted a little toward the world's palette
uniform bool cartoon = true;
// the medium square's hole over the near one (as body_globe.gdshader's)
uniform vec3 hole_centre = vec3(0.0);
uniform vec3 hole_up = vec3(0.0, 1.0, 0.0);
uniform vec3 hole_east = vec3(1.0, 0.0, 0.0);
uniform float hole_half = 0.0;

// lamps on the ground (#1942, World/Bodies/GroundLamps.cs), as body_globe.gdshader's
uniform int lamp_count = 0;
uniform vec4 lamp_pos[16];
uniform vec4 lamp_dir[16];
uniform vec4 lamp_col[16];

varying vec3 world_normal;
varying vec3 world_pos;

void vertex() {
	world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	if (hole_half > 0.0) {
		vec3 d = world_pos - hole_centre;
		vec3 south = cross(hole_east, hole_up);
		if (abs(dot(d, hole_east)) < hole_half && abs(dot(d, south)) < hole_half) discard;
	}
	vec3 photo = texture(albedo_tex, UV).rgb;
	vec3 col = cartoon ? pow(max(photo, vec3(0.0)), vec3(0.8)) : photo;
	vec3 lamps = vec3(0.0);
	for (int i = 0; i < lamp_count; i++) {
		vec3 to = world_pos - lamp_pos[i].xyz;
		float d = length(to);
		float fall = clamp(1.0 - d / lamp_pos[i].w, 0.0, 1.0);
		vec3 l = to / max(d, 0.001);
		float cone = lamp_dir[i].w > 0.0 ? smoothstep(lamp_dir[i].w, mix(lamp_dir[i].w, 1.0, 0.25), dot(l, lamp_dir[i].xyz)) : 1.0;
		lamps += lamp_col[i].rgb * fall * fall * cone * max(dot(normalize(world_normal), -l), 0.0) * 0.1;
	}
	ALBEDO = col;
	// the mesh's own up, whichever side is drawn: two-sided, the engine flips NORMAL on what it takes for a back face, and
	// the squares' triangles wind so that their top is one (the unshaded globe never read NORMAL)
	NORMAL = normalize((VIEW_MATRIX * vec4(world_normal, 0.0)).xyz);
	EMISSION = col * (night_floor + lamps);
}

void light() {
	// the sun (or the Earth at night) only: plain Lambert, its shadow, the air's hue through a low sun
	if (LIGHT_IS_DIRECTIONAL) {
		float l = max(dot(NORMAL, LIGHT), 0.0);
		float e = max(dot(NORMAL, VIEW), 0.05);
		float shade = mix(l, 2.0 * l / (l + e), lunar);
		vec3 through = exp(-(air_tau - vec3(min(min(air_tau.r, air_tau.g), air_tau.b))) / max(l, 0.05));
		DIFFUSE_LIGHT += light_tint * light_strength * shade * ATTENUATION * through;
	}
}

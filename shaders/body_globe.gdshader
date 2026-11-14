// Another body's globe (#1848, World/Bodies/BodyGlobe.cs): its pyramid's tiles in real geometry, lit by its own
// sun (the direction from the body to the Sun in world axes, set each frame), so the Moon's terminator is where the
// ephemeris puts it whatever the anchor's body. No air, no haze: the Moon's ground is as sharp at 300 km as at 3.
// The colour is the body's photo (the CGI Moon Kit's Hapke-normalised LROC colour: no shadows of its own), lifted
// for Cartoon as the Earth's globe lifts Blue Marble.
shader_type spatial;
render_mode unshaded, cull_disabled, fog_disabled;

uniform sampler2D albedo_tex : source_color, filter_linear_mipmap, repeat_disable;
// world axes, unit, toward the Sun from the body
uniform vec3 sun_dir = vec3(0.0, 1.0, 0.0);
// what the night side still gets (earthshine on the near side, starlight): the plan's floor, so a two-week
// lunar night stays playable
uniform float night_floor = 0.06;
// the sun's strength on ground square to it
uniform float sun_strength = 1.6;
// Cartoon (the default style): the photo's albedo lifted a little toward the world's palette
uniform bool cartoon = true;

varying vec3 world_normal;

void vertex() {
	world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

void fragment() {
	vec3 photo = texture(albedo_tex, UV).rgb;
	// the Moon's real albedo is ~0.12: lifted by a gentle power so its maria and highlands both read
	vec3 col = cartoon ? pow(max(photo, vec3(0.0)), vec3(0.8)) : photo;
	// plain Lambert: the relief reads by its slopes, and a wall turned from the sun falls to the floor (the Moon's
	// shadows are black, there is no sky to fill them)
	float lambert = max(dot(normalize(world_normal), normalize(sun_dir)), 0.0);
	ALBEDO = col * (night_floor + sun_strength * lambert);
}

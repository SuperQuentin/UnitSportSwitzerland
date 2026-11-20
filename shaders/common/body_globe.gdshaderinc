// The globe of another body (#1848): see body_globe.gdshader. Shared by its stand-in (body_globe_standin.gdshader, #2051).

uniform sampler2D albedo_tex : source_color, filter_linear_mipmap, repeat_disable;
// world axes, unit, toward the Sun from the body
uniform vec3 sun_dir = vec3(0.0, 1.0, 0.0);
// what the night side still gets (earthshine on the near side, starlight): the plan's floor, so a two-week
// lunar night stays playable
uniform float night_floor = 0.06;
// the sun's strength on ground square to it
uniform float sun_strength = 1.6;
// the air's vertical optical depth per channel (#1854: Mars, Venus, Titan; zero without air): the sunlight that reaches
// the ground takes the air's hue, exp(-(tau - its least channel) / cos zenith): the least-dimmed channel keeps its full
// strength (the game's ground stays playable under Titan's haze), the others fall away, more with a low sun (redder)
uniform vec3 air_tau = vec3(0.0);
// a giant's terminator (#1854): its deep air carries the light past the line (a wrap of the sun's term), so the edge of
// its day is a band, not the hard line a clipped lit side over the night floor drew (Jupiter from Io); 0 for solid ground
uniform float terminator_wrap = 0.0;
// Cartoon (the default style): the photo's albedo lifted a little toward the world's palette
uniform bool cartoon = true;
// the near ground's square (World/Bodies/BodyGround, #1850): the globe's own coarse cells step aside over it, a
// square column along its up (whatever the cells' height), its half side hole_half; 0: no hole. The square's edge is
// eased into these cells, so the two meet there without a step
uniform vec3 hole_centre = vec3(0.0);
uniform vec3 hole_up = vec3(0.0, 1.0, 0.0);
uniform vec3 hole_east = vec3(1.0, 0.0, 0.0);
uniform float hole_half = 0.0;
// the body under the camera (#2024): its centre in world space, so a slope past the terminator (the sun under its own
// horizon) stays in the night: unshaded, a far ridge facing a sun 10° down was lit as by day over the dark plain
uniform bool horizon_shade = false;
// the regolith's own law on the ground (#2024, as body_ground.gdshader's lunar); 0 from space, where the sky's sphere of the
// body (globe_look) is lit as these tiles are
uniform float lunar = 0.0;
uniform vec3 body_centre = vec3(0.0);

// lamps on the ground (#1942, World/Bodies/GroundLamps.cs): a flood light, a colony's lamps; this shader is unshaded,
// so a Light3D never reaches it. pos: world xyz and range; dir: a spot's direction and the cosine of its half cone
// (w 0: all round); col: colour times energy
uniform int lamp_count = 0;
uniform vec4 lamp_pos[16];
uniform vec4 lamp_dir[16];
uniform vec4 lamp_col[16];

varying vec3 world_normal;
varying vec3 world_pos;

#ifdef SKY_STANDIN
#include "res://shaders/common/sky_cover.gdshaderinc"
// the body's centre in this tile's own axes (World/Bodies/BodyGlobe.cs, set once): the far half of the body faces away
uniform vec3 local_centre = vec3(0.0);
varying vec3 centre_world;
varying float shrink;
#endif

void vertex() {
	world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
#ifdef SKY_STANDIN
	centre_world = (MODEL_MATRIX * vec4(local_centre, 1.0)).xyz;
	// the root's shrink about the camera (the tile's own axes are orthonormal)
	shrink = length(MODEL_MATRIX[0].xyz);
#endif
}

void fragment() {
#ifdef SKY_STANDIN
	// drawn in order, not by depth (sky_cover): the half turned away is never seen from outside
	vec3 cam = INV_VIEW_MATRIX[3].xyz;
	if (dot(world_pos - centre_world, cam - world_pos) < 0.0) discard;
	vec3 to = world_pos - cam;
	float shown = length(to);
	if (sky_covered(SCREEN_UV, cam, to / max(shown, 1e-6), shown / max(shrink, 1e-30), INV_PROJECTION_MATRIX)) discard;
#endif
	if (hole_half > 0.0) {
		vec3 d = world_pos - hole_centre;
		vec3 south = cross(hole_east, hole_up);
		if (abs(dot(d, hole_east)) < hole_half && abs(dot(d, south)) < hole_half) discard;
	}
	vec3 photo = texture(albedo_tex, UV).rgb;
	// the Moon's real albedo is ~0.12: lifted by a gentle power so its maria and highlands both read
	vec3 col = cartoon ? pow(max(photo, vec3(0.0)), vec3(0.8)) : photo;
	// plain Lambert: the relief reads by its slopes, and a wall turned from the sun falls to the floor (the Moon's
	// shadows are black, there is no sky to fill them)
	float lambert = max((dot(normalize(world_normal), normalize(sun_dir)) + terminator_wrap) / (1.0 + terminator_wrap), 0.0);
	lambert *= lambert * terminator_wrap + (1.0 - terminator_wrap) * sign(lambert);
	if (horizon_shade) lambert *= smoothstep(-0.03, 0.02, dot(normalize(world_pos - body_centre), normalize(sun_dir)));
	if (lunar > 0.0) {
		float e = max(dot(normalize(world_normal), normalize((INV_VIEW_MATRIX * vec4(VIEW, 0.0)).xyz)), 0.05);
		lambert = mix(lambert, 2.0 * lambert / (lambert + e), lunar);
	}
	vec3 lamps = vec3(0.0);
	for (int i = 0; i < lamp_count; i++) {
		vec3 to = world_pos - lamp_pos[i].xyz;
		float d = length(to);
		float fall = clamp(1.0 - d / lamp_pos[i].w, 0.0, 1.0);
		vec3 l = to / max(d, 0.001);
		float cone = lamp_dir[i].w > 0.0 ? smoothstep(lamp_dir[i].w, mix(lamp_dir[i].w, 1.0, 0.25), dot(l, lamp_dir[i].xyz)) : 1.0;
		lamps += lamp_col[i].rgb * fall * fall * cone * max(dot(normalize(world_normal), -l), 0.0) * 0.1;
	}
	vec3 through = exp(-(air_tau - vec3(min(min(air_tau.r, air_tau.g), air_tau.b))) / max(lambert, 0.05));
	ALBEDO = col * (night_floor + sun_strength * lambert * through + lamps);
#ifdef SKY_STANDIN
	// solid: it covers the stars drawn before it, its night side dark but whole
	ALPHA = 1.0;
#endif
}

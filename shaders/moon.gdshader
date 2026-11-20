shader_type spatial;

// The real moon in the sky (#1440, src/World/MoonDisc.cs): its face from NASA's LROC colour map,
// turned by its own axes (libration included) and lit by the real sun, so its phase is right.
// Added onto the sky by day: its unlit part lets the sky through, as the real one does. Drawn after the stars and
// covering them where the sky is dark (#2051; it wrote its depth a step nearer than the stars, #1798, but high up a
// depth that near the far plane rounded to the stars' own and they shone through it again).
render_mode unshaded, fog_disabled, blend_premul_alpha, depth_draw_never, cull_back, shadows_disabled;

uniform sampler2D albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
// world axes, toward the sun
uniform vec3 sun_dir = vec3(0.0, 1.0, 0.0);
// world axes: columns are the moon's 0° N 0° E, 90° E and its north pole
uniform mat3 face = mat3(1.0);
// day 0.45, night 1 (the sky behind it does the rest)
uniform float brightness = 1.0;
// the night side, faintly lit by the Earth: a crescent's dark disc shows beside it (0.012 vanished into the night sky, #1798)
uniform float earthshine = 0.04;

// the colonies' lights (#1946, src/World/Moon/ColonyLights.cs): their directions in the moon's own axes (as face's
// columns), their strength; a spark a pixel or two across on the dark part, whatever the zoom (the colony's own ~1 km when
// the telescope shows more than that)
uniform vec4 colonies[8];
uniform int colony_count = 0;


// seen from space (#1998, World/Bodies/BodyLook.cs): the body's globe's own look, so the hand-over between this sphere and
// its globe never shows: the photo lifted for Cartoon, plain Lambert (a giant's wrapped terminator), the night floor, the
// globe's exposure. 0: the sky's own look (through the air, the telescope by day)
uniform float globe_look = 0.0;
uniform float sun_strength = 1.6;
uniform float night_floor = 0.06;
uniform float terminator_wrap = 0.0;

vec3 globe_lit(vec3 photo, vec3 n, vec3 sun) {
	vec3 col = pow(max(photo, vec3(0.0)), vec3(0.8));
	float lambert = max((dot(n, normalize(sun)) + terminator_wrap) / (1.0 + terminator_wrap), 0.0);
	lambert *= lambert * terminator_wrap + (1.0 - terminator_wrap) * sign(lambert);
	return col * (night_floor + sun_strength * lambert);
}

varying vec3 world_normal;

// a stand-in for a far body (#2051, shaders/common/sky_cover.gdshaderinc): drawn after the stars, it covers them as far
// as the sky is dark (in space, at night); by day it is added onto the sky as before. It steps aside for the scene in
// front of it; its true distance (metres) decides against the anchor's own body
#include "res://shaders/common/sky_cover.gdshaderinc"
uniform float true_distance = 1e12;
global uniform float world_night;
global uniform float world_space;
varying vec3 cover_world;

float sky_dark() {
	return max(smoothstep(0.35, 0.95, world_night), world_space);
}

#include "res://shaders/common/sky_depth.gdshaderinc"

void vertex() {
	world_normal = (MODEL_MATRIX * vec4(VERTEX, 0.0)).xyz;
	cover_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	POSITION = sky_depth(PROJECTION_MATRIX * (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)));
	POSITION.z = max(POSITION.z, POSITION.w * SKY_MIN_DEPTH * 2.0);
}

void fragment() {
	vec3 cam = INV_VIEW_MATRIX[3].xyz;
	if (sky_covered(SCREEN_UV, cam, normalize(cover_world - cam), true_distance, INV_PROJECTION_MATRIX)) discard;
	ALPHA = sky_dark();
	vec3 n = normalize(world_normal);
	vec3 b = vec3(dot(n, face[0]), dot(n, face[1]), dot(n, face[2]));
	vec2 uv = vec2(0.5 + atan(b.y, b.x) / (2.0 * PI), 0.5 - asin(clamp(b.z, -1.0, 1.0)) / PI);
	vec3 surface = texture(albedo, uv).rgb;
	// the full moon is a flat disc, not a shaded ball: lit or not, with a soft terminator
	float lit = smoothstep(-0.02, 0.06, dot(n, sun_dir));
	ALBEDO = mix(surface * (lit + earthshine) * brightness, globe_lit(surface, n, sun_dir), globe_look);
	float pixel = max(length(fwidth(b)), 1e-7);
	for (int i = 0; i < colony_count; i++) {
		vec3 l = colonies[i].xyz;
		float px = length(b - l) / pixel;
		float size = max(1.3, 0.0006 / pixel);
		float spark = (1.0 - smoothstep(size * 0.35, size, px)) * colonies[i].w * (1.0 - lit);
		ALBEDO += vec3(1.0, 0.82, 0.5) * spark * 1.8;
	}
}

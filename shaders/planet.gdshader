shader_type spatial;

// A planet or a big moon at its true size in the sky (#1444, src/World/SkyBodies.cs). The mesh is a
// unit sphere whose local X, Y, Z the node's basis turns onto the body's own 0° / 90° E / north
// pole (and squashes along the pole: Jupiter, Saturn), so the map is read from the vertex itself.
// Lit by the real sun: Venus and Mercury show their phases. Added onto the sky by day, as the moon, and covering the
// stars where the sky is dark (#2051: drawn after them; added, the stars shone through its dark side).
render_mode unshaded, fog_disabled, blend_premul_alpha, depth_draw_never, cull_back, shadows_disabled;

uniform sampler2D albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool textured = true;
uniform vec4 tint : source_color = vec4(1.0);
// world axes, toward the sun
uniform vec3 sun_dir = vec3(0.0, 1.0, 0.0);
uniform float brightness = 1.0;
// the disc's share of the point-to-disc fade (in brightness too); the globe look takes it alone
uniform float fade = 1.0;
// limb darkening, for the gas giants' cloud tops
uniform float limb = 0.0;
// a body this one hides behind (a big moon behind its planet; the rings behind Saturn): world centre and radius, 0 = none
uniform vec4 occluder = vec4(0.0);


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

varying vec3 local_dir;
varying vec3 world_normal;
varying vec3 view_dir;
varying vec3 world_pos;

// behind the occluder as seen from the camera: hidden
bool occluded(vec3 world, vec3 cam) {
	if (occluder.w <= 0.0) return false;
	vec3 to_c = occluder.xyz - cam;
	vec3 to_p = world - cam;
	float along = dot(to_p, normalize(to_c));
	if (along < length(to_c)) return false;
	vec3 off = to_p * (length(to_c) / max(along, 1e-6)) - to_c;
	return length(off) < occluder.w;
}


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
	POSITION = sky_depth(PROJECTION_MATRIX * (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)));
	local_dir = VERTEX;
	world_normal = (MODEL_MATRIX * vec4(VERTEX, 0.0)).xyz;
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	view_dir = world_pos - INV_VIEW_MATRIX[3].xyz;
}

void fragment() {
	if (occluded(world_pos, INV_VIEW_MATRIX[3].xyz)) discard;
	vec3 cam = INV_VIEW_MATRIX[3].xyz;
	if (sky_covered(SCREEN_UV, cam, normalize(world_pos - cam), true_distance, INV_PROJECTION_MATRIX)) discard;
	ALPHA = sky_dark() * fade;
	vec3 b = normalize(local_dir);
	vec2 uv = vec2(0.5 + atan(b.y, b.x) / (2.0 * PI), 0.5 - asin(clamp(b.z, -1.0, 1.0)) / PI);
	vec3 surface = textured ? texture(albedo, uv).rgb : vec3(1.0);
	vec3 n = normalize(world_normal);
	float lit = smoothstep(-0.02, 0.1, dot(n, sun_dir));
	float mu = clamp(dot(n, -normalize(view_dir)), 0.0, 1.0);
	float dark = mix(1.0, 0.35 + 0.65 * mu, limb);
	ALBEDO = mix(surface * tint.rgb * lit * dark * brightness, globe_lit(surface * tint.rgb, n, sun_dir) * fade, globe_look);
}

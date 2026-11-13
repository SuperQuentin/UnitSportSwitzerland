shader_type spatial;

// A planet or a big moon at its true size in the sky (#1444, src/World/SkyBodies.cs). The mesh is a
// unit sphere whose local X, Y, Z the node's basis turns onto the body's own 0° / 90° E / north
// pole (and squashes along the pole: Jupiter, Saturn), so the map is read from the vertex itself.
// Lit by the real sun: Venus and Mercury show their phases. Added onto the sky, as the moon.
render_mode unshaded, fog_disabled, blend_add, depth_draw_never, cull_back, shadows_disabled;

uniform sampler2D albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool textured = true;
uniform vec4 tint : source_color = vec4(1.0);
// world axes, toward the sun
uniform vec3 sun_dir = vec3(0.0, 1.0, 0.0);
uniform float brightness = 1.0;
// limb darkening, for the gas giants' cloud tops
uniform float limb = 0.0;
// a body this one hides behind (a big moon behind its planet; the rings behind Saturn): world centre and radius, 0 = none
uniform vec4 occluder = vec4(0.0);

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
	vec3 b = normalize(local_dir);
	vec2 uv = vec2(0.5 + atan(b.y, b.x) / (2.0 * PI), 0.5 - asin(clamp(b.z, -1.0, 1.0)) / PI);
	vec3 surface = textured ? texture(albedo, uv).rgb : vec3(1.0);
	vec3 n = normalize(world_normal);
	float lit = smoothstep(-0.02, 0.1, dot(n, sun_dir));
	float mu = clamp(dot(n, -normalize(view_dir)), 0.0, 1.0);
	float dark = mix(1.0, 0.35 + 0.65 * mu, limb);
	ALBEDO = surface * tint.rgb * lit * dark * brightness;
}

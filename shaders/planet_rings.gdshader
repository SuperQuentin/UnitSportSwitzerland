shader_type spatial;

// Saturn's rings (#1444, src/World/SkyBodies.cs): a flat annulus in its equator, the radial strip
// of the ring map (inner edge left, outer edge right) read by the distance from the centre, lit by
// the sun on both faces; the part behind Saturn is hidden. Saturn's shadow on them is left out.
render_mode unshaded, fog_disabled, blend_add, depth_draw_never, cull_disabled, shadows_disabled;

uniform sampler2D rings : source_color, filter_linear_mipmap, repeat_disable;
// the inner edge as a share of the outer
uniform float inner = 0.53;
uniform float brightness = 1.0;
uniform vec3 sun_dir = vec3(0.0, 1.0, 0.0);
uniform vec3 pole = vec3(0.0, 0.0, 1.0);
// a body this one hides behind (a big moon behind its planet; the rings behind Saturn): world centre and radius, 0 = none
uniform vec4 occluder = vec4(0.0);

varying vec2 flat_pos;
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
	flat_pos = VERTEX.xy;
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	float r = length(flat_pos);
	if (r < inner || r > 1.0 || occluded(world_pos, INV_VIEW_MATRIX[3].xyz)) discard;
	vec4 c = texture(rings, vec2((r - inner) / (1.0 - inner), 0.5));
	// the sun lights the rings by how high it stands over their plane
	float lit = 0.35 + 0.65 * abs(dot(normalize(sun_dir), normalize(pole)));
	ALBEDO = c.rgb * c.a * lit * brightness;
}

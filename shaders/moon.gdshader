shader_type spatial;

// The real moon in the sky (#1440, src/World/MoonDisc.cs): its face from NASA's LROC colour map,
// turned by its own axes (libration included) and lit by the real sun, so its phase is right.
// Added onto the sky: its unlit part lets the sky through, as the real one does by day.
render_mode unshaded, fog_disabled, blend_add, depth_draw_never, cull_back, shadows_disabled;

uniform sampler2D albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
// world axes, toward the sun
uniform vec3 sun_dir = vec3(0.0, 1.0, 0.0);
// world axes: columns are the moon's 0° N 0° E, 90° E and its north pole
uniform mat3 face = mat3(1.0);
// day 0.45, night 1 (the sky behind it does the rest)
uniform float brightness = 1.0;
// the night side, faintly lit by the Earth
uniform float earthshine = 0.012;

varying vec3 world_normal;

#include "res://shaders/common/sky_depth.gdshaderinc"

void vertex() {
	world_normal = (MODEL_MATRIX * vec4(VERTEX, 0.0)).xyz;
	POSITION = sky_depth(PROJECTION_MATRIX * (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)));
}

void fragment() {
	vec3 n = normalize(world_normal);
	vec3 b = vec3(dot(n, face[0]), dot(n, face[1]), dot(n, face[2]));
	vec2 uv = vec2(0.5 + atan(b.y, b.x) / (2.0 * PI), 0.5 - asin(clamp(b.z, -1.0, 1.0)) / PI);
	vec3 surface = texture(albedo, uv).rgb;
	// the full moon is a flat disc, not a shaded ball: lit or not, with a soft terminator
	float lit = smoothstep(-0.02, 0.06, dot(n, sun_dir));
	ALBEDO = surface * (lit + earthshine) * brightness;
}

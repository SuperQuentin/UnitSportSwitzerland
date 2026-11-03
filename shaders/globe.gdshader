// The globe from far out (#1430 H4, World/Globe/Globe.cs): the coarse cube tiles in real geometry,
// so no curve_offset (the flat tiles are bent onto a sphere; these already are one). Drawn a little
// behind whatever stands at the same place, so the flat world's tiles and horizon blocks win where
// they are, and the globe shows round and past them.
shader_type spatial;
render_mode cull_back, specular_disabled, fog_disabled;

uniform sampler2D albedo_tex : source_color, filter_linear_mipmap, repeat_disable;
// how far behind, as a fraction of the distance from the eye
uniform float push = 0.004;

void vertex() {
	vec4 view = MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
	view.xyz *= 1.0 + push;
	POSITION = PROJECTION_MATRIX * view;
}

void fragment() {
	ALBEDO = texture(albedo_tex, UV).rgb;
	ROUGHNESS = 1.0;
	METALLIC = 0.0;
}

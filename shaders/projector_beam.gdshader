// A cinema projector's beam (#1238, Interiors/InteriorNode.Cinema): added light, brightest at the lens,
// the vertex colour's alpha its strength. Shown only while the screen is on.
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled, shadows_disabled;

void fragment() {
	ALBEDO = COLOR.rgb * COLOR.a;
}

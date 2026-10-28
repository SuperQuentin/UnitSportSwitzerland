// The hole a cinema's screen becomes while its video is on a compositor layer in the headset (#1319,
// InteriorNode.CinemaLayer): the layer is behind the 3D picture, and the picture is see-through here. Multiplied into
// what is drawn, colour and alpha by 0; depth-tested, so a head, a seat, a curtain or a hand nearer keeps covering the
// layer (as xr_panel_hole, the menus' hole, with the whole quad shown).
shader_type spatial;
render_mode unshaded, blend_mul, depth_draw_never, cull_back, shadows_disabled, fog_disabled;

void fragment() {
	ALBEDO = vec3(0.0);
	ALPHA = 0.0;
}

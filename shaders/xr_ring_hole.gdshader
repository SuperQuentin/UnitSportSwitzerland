shader_type spatial;
// The hole the watch's wheel shows through (XR/XrWristRing, #1112), as the panel's (xr_panel_hole.gdshader): its
// compositor layer is behind the 3D picture, and the picture is see-through on the wheel's glass disc as much as
// the glass is opaque. The disc is known (Ui/Radial draws it first, under the wedges and the names), so no mask:
// a hand or anything nearer (depth-tested) keeps covering the wheel.
render_mode unshaded, blend_mul, depth_draw_never, cull_disabled, shadows_disabled, fog_disabled;

/// The glass disc's radius in the quad's UV (1 = the quad's width) and how opaque it is.
uniform float radius = 0.43;
uniform float opacity = 0.78;

void fragment() {
	float r = length(UV - vec2(0.5));
	float shown = opacity * (1.0 - smoothstep(radius - 0.004, radius, r));
	ALBEDO = vec3(1.0 - shown);
	ALPHA = 1.0 - shown;
}

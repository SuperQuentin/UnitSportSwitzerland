shader_type spatial;
// The hole the VR panel shows through (XR/XrUi, #1112): the panel is a compositor layer behind the 3D picture, and the
// picture is see-through here exactly as much as the panel's own pixel is opaque. Multiplied into what is drawn:
// colour and alpha by (1 - the panel's alpha), so the world stays where the HUD is clear and a hand, the pointer or
// anything nearer (depth-tested) keeps covering the panel. Godot's own hole is an opaque rectangle: a HUD with clear
// parts showed a black box round it.
render_mode unshaded, blend_mul, depth_draw_never, cull_disabled, shadows_disabled, fog_disabled;

uniform sampler2D panel : hint_default_transparent, filter_linear;
/// What leaves the picture as it is: the Mobile renderer keeps colour at half scale in its 3D buffer, so a multiplied
/// 1 would halve the world under the whole quad (XrUi.HoleWhite, 2 there).
uniform float white = 1.0;

void fragment() {
	float shown = texture(panel, UV).a;
	ALBEDO = vec3(white * (1.0 - shown));
	ALPHA = 1.0 - shown;
}

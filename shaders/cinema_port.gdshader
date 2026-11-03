// The picture going through a cinema's porthole (#1279, #1298): faint, seen from both sides (from the seats the screen's
// picture mirrored, as light going through glass toward you is), framed as the hall's projector is set (cinema_display),
// so the glass shows what the screen shows. A 3D film gives it one eye.
shader_type spatial;
render_mode unshaded, cull_disabled, shadows_disabled, blend_mix;

#include "res://shaders/cinema_display.gdshaderinc"

uniform sampler2D picture : source_color, filter_linear, repeat_disable;
uniform float alpha = 0.45;

void fragment() {
	vec2 at;
	bool shown = display_at(UV, at);
	ALBEDO = shown ? texture(picture, at).rgb : vec3(0.0);
	ALPHA = alpha;
}

// A cinema hall's video for the headset's compositor layer (#1319, InteriorNode.CinemaLayer): the decoded frame as the
// hall's projector is set (cinema_display, the same mapping as cinema_screen), drawn into a SubViewport the frame's size
// that an OpenXR quad layer shows where the screen is. 2D films only.
shader_type canvas_item;
render_mode unshaded;

#include "res://shaders/cinema_display.gdshaderinc"

uniform sampler2D picture : source_color, filter_linear, repeat_disable;

void fragment() {
	vec2 at;
	COLOR = display_at(UV, at) ? vec4(texture(picture, at).rgb, 1.0) : vec4(0.0, 0.0, 0.0, 1.0);
}

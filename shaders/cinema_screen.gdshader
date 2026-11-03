// A video on a cinema hall's screen (#1298): the picture as the hall's projector is set (cinema_display: fitted into a
// frame of the chosen ratio, contained, covering or stretched; black outside it). A 3D film holds both eyes side by side
// (left half the left eye): in a headset each eye sees its own half (VIEW_INDEX), on a flat display red-cyan anaglyph
// with Dubois' least-squares matrices, or the left eye alone for a viewer without glasses. Unshaded, as the slides'
// picture: the screen is its own light.
shader_type spatial;
render_mode unshaded, cull_back, shadows_disabled;

#include "res://shaders/cinema_display.gdshaderinc"

uniform sampler2D picture : source_color, filter_linear, repeat_disable;
// both eyes side by side
uniform bool stereo = false;
// in a headset: each eye its half; on a flat display: red-cyan glasses or the left eye alone
uniform bool headset = false;
uniform bool anaglyph = true;

// Dubois, red-cyan (E. Dubois, "A projection method to generate anaglyph stereo images", 2001), rows = output R, G, B
const mat3 LEFT = mat3(vec3(0.456, -0.040, -0.015), vec3(0.500, -0.038, -0.021), vec3(0.176, -0.016, -0.005));
const mat3 RIGHT = mat3(vec3(-0.043, 0.378, -0.072), vec3(-0.088, 0.734, -0.113), vec3(-0.002, -0.018, 1.226));

vec3 eye(vec2 at, float half_offset) {
	return texture(picture, stereo ? vec2(half_offset + at.x * 0.5, at.y) : at).rgb;
}

void fragment() {
	vec2 at;
	vec3 col = vec3(0.0);
	if (display_at(UV, at)) {
		vec3 l = eye(at, 0.0);
		if (!stereo) col = l;
		else {
			vec3 r = eye(at, 0.5);
			if (headset) col = VIEW_INDEX == VIEW_RIGHT ? r : l;
			else if (anaglyph) col = clamp(LEFT * l + RIGHT * r, 0.0, 1.0);
			else col = l;
		}
	}
	ALBEDO = col;
}

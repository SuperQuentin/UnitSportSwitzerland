// A 3D film on a cinema hall's screen (#1298 phase 6). The picture holds both eyes side by side (left half the left
// eye, right half the right: CinemaVideo packs every stereo source so). In a headset each eye sees its own half
// (VIEW_INDEX); on a flat display red-cyan anaglyph with Dubois' least-squares matrices (the glasses' filters), or the
// left eye alone for a viewer without glasses. Unshaded, as the slides' picture: the screen is its own light.
shader_type spatial;
render_mode unshaded, cull_back, shadows_disabled;

uniform sampler2D picture : source_color, filter_linear, repeat_disable;
// 1 in a headset: each eye its half; 0 on a flat display
uniform bool headset = false;
// on a flat display: red-cyan glasses (true) or the left eye alone
uniform bool anaglyph = true;

// Dubois, red-cyan (E. Dubois, "A projection method to generate anaglyph stereo images", 2001), rows = output R, G, B
const mat3 LEFT = mat3(vec3(0.456, -0.040, -0.015), vec3(0.500, -0.038, -0.021), vec3(0.176, -0.016, -0.005));
const mat3 RIGHT = mat3(vec3(-0.043, 0.378, -0.072), vec3(-0.088, 0.734, -0.113), vec3(-0.002, -0.018, 1.226));

void fragment() {
	vec3 l = texture(picture, vec2(UV.x * 0.5, UV.y)).rgb;
	vec3 r = texture(picture, vec2(0.5 + UV.x * 0.5, UV.y)).rgb;
	vec3 c;
	if (headset) {
		c = VIEW_INDEX == VIEW_RIGHT ? r : l;
	} else if (anaglyph) {
		c = clamp(LEFT * l + RIGHT * r, 0.0, 1.0);
	} else {
		c = l;
	}
	ALBEDO = c;
}

// The phone map's relief and background (#1826): the relief ramp is dark, and at 60 % over a busy world it read as a murky blur.
// Lifts the tones and spreads them so valley floor, slope and ridge, lakes and rivers read; alpha is the canvas's self modulate.
shader_type canvas_item;

uniform float gamma = 0.62;
uniform float contrast = 1.45;
uniform float gain = 1.25;

void fragment() {
	vec3 c = pow(max(COLOR.rgb, vec3(0.0)), vec3(gamma));
	c = (c - 0.45) * contrast + 0.45;
	COLOR = vec4(clamp(c * gain, 0.0, 1.0), COLOR.a);
}

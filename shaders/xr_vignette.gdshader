// VR comfort vignette (#186): darkens the edge of each eye's view while the world moves or turns
// under the player. Drawn as a full-view quad straight in clip space, once per eye (multiview).
shader_type spatial;
render_mode unshaded, depth_test_disabled, depth_draw_never, cull_disabled, blend_mix, shadows_disabled, fog_disabled;

// 0 = no vignette, 1 = only a small window left in the middle
uniform float strength : hint_range(0.0, 1.0) = 0.0;
// 0 = clear, 1 = all black: a blink to hide a cut (XrRig.Blink, the crash view #214)
uniform float blackout : hint_range(0.0, 1.0) = 0.0;

void vertex() {
	// the quad is 2x2 in its own space: put it over the whole view, nearest the eye
	POSITION = vec4(VERTEX.xy, 1.0, 1.0);
}

void fragment() {
	vec2 d = (SCREEN_UV - 0.5) * vec2(1.0, 1.1);
	float r = length(d) * 2.0;
	// the clear window shrinks from the whole view to ~40% of it
	float inner = mix(1.25, 0.40, strength);
	float a = smoothstep(inner, inner + 0.35, r);
	ALBEDO = vec3(0.0);
	ALPHA = max(a * clamp(strength * 3.0, 0.0, 1.0), blackout);
}

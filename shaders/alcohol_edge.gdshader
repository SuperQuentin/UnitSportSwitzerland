// Drunk edge blur (#1271): the rim of the screen softens, the middle stays sharp. A full-screen canvas item over the
// 3D view, shown only while drunk (strength 0 hides it), the strength set on change.
shader_type canvas_item;

uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap;
// 0 = off, 1 = a strong blur from the rim inwards
uniform float strength : hint_range(0.0, 1.0) = 0.0;

void fragment() {
	vec2 d = (SCREEN_UV - 0.5) * vec2(1.0, 0.8);
	float r = clamp((length(d) * 2.0 - 0.45) / 0.55, 0.0, 1.0);
	float lod = r * r * strength * 4.0;
	COLOR = vec4(textureLod(screen_tex, SCREEN_UV, lod).rgb, 1.0);
}

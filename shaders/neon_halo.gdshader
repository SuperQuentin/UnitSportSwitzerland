// A neon sign's glow (#1232, Interiors/NeonSign), for styles with no bloom of their own (Cartoon, PS1):
// light added on the wall behind the sign, an ellipse over the quad's UV in the sign's colour.
// Faint by day, full at night (world_night, DayNight). Nothing is set per frame.
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled, shadows_disabled;

global uniform float world_night;
uniform vec4 tint : source_color = vec4(1.0);
uniform float strength = 0.4;

void fragment() {
	vec2 d = (UV - 0.5) * 2.0;
	float a = 1.0 - smoothstep(0.0, 1.0, length(d));
	ALBEDO = tint.rgb * a * a * strength * mix(0.06, 1.0, world_night);
}

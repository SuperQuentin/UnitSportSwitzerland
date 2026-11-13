shader_type spatial;

// The real sun in the sky (#1479, src/World/SunDisc.cs): the look of shaders/common/sun_look.gdshaderinc on a
// quad square to the sun's direction just inside the far plane, its axes the screen's. Added onto the sky (the
// sky no longer draws the light under the real sun), so its corona is a glow on the blue, not a patch over it.
// The depth as the moon's: a ridge, a tree or a building hides it.
render_mode unshaded, fog_disabled, blend_add, depth_draw_never, cull_disabled, shadows_disabled;

// the disc's angular radius, rad (true 0.00465; at least the moon's least share of the view)
uniform float core_r = 0.00465;
// the quad's angular half-size, rad (SunDisc.Reach)
uniform float reach = 0.14;
// 0 high in the sky, 1 on the horizon: more air, a warmer and wider corona
uniform float warm = 0.0;
// faded out above the air (the atmosphere's sun takes over) and under the horizon
uniform float brightness = 1.0;
// the starburst: faint on the ground, gone once the disc is big on screen (a telescope's view)
uniform float spikes = 0.15;

#include "res://shaders/common/sky_depth.gdshaderinc"
#include "res://shaders/common/sun_look.gdshaderinc"

void vertex() {
	POSITION = sky_depth(PROJECTION_MATRIX * (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)));
}

void fragment() {
	// the offset from the sun on the quad's tangent plane: radians at these sizes, x right and y up as the screen
	vec2 t = vec2(UV.x * 2.0 - 1.0, 1.0 - UV.y * 2.0) * tan(reach);
	float a = length(t);
	vec3 c = sun_look(t, core_r, warm, spikes, max(fwidth(a), 1e-6));
	// faded to nothing at the quad's edge
	c *= 1.0 - smoothstep(reach * 0.6, reach * 0.95, a);
	ALBEDO = c * brightness;
}

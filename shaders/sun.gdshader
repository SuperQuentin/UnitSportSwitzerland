shader_type spatial;

// The real sun in the sky (#1479, src/World/SunDisc.cs): a limb-darkened disc at its angular radius and a corona,
// on a quad square to the sun's direction just inside the far plane. Added onto the sky (the Cartoon sky no longer
// draws the light, #1479), so its corona is a glow on the blue, not a patch over it. The depth as the moon's:
// a ridge, a tree or a building hides it.
render_mode unshaded, fog_disabled, blend_add, depth_draw_never, cull_disabled, shadows_disabled;

// the disc's angular radius, rad (true 0.00465; at least the moon's least share of the view)
uniform float core_r = 0.00465;
// the quad's angular half-size, rad (SunDisc.Reach)
uniform float reach = 0.14;
// 0 high in the sky, 1 on the horizon: more air, a warmer and wider corona
uniform float warm = 0.0;
// faded out above the air (the atmosphere's sun takes over) and under the horizon
uniform float brightness = 1.0;

#include "res://shaders/common/sky_depth.gdshaderinc"

void vertex() {
	POSITION = sky_depth(PROJECTION_MATRIX * (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)));
}

void fragment() {
	// the angle off the sun's centre, from the quad's tangent plane
	vec2 t = (UV * 2.0 - 1.0) * tan(reach);
	float a = atan(length(t));
	float aa = max(fwidth(a), 1e-6);
	// the disc, limb-darkened (the sun's edge is ~60 % of its middle)
	float r = clamp(a / core_r, 0.0, 1.0);
	float limb = 1.0 - 0.4 * (1.0 - sqrt(max(1.0 - r * r, 0.0)));
	float disc = 1.0 - smoothstep(core_r - aa, core_r + aa, a);
	// the corona: a bright halo close in and a wide, faint glow, both wider through more air
	float spread = 1.0 + 1.5 * warm;
	float corona = 0.55 * exp(-(a - core_r) / (0.012 * spread)) + 0.14 * exp(-(a - core_r) / (0.06 * spread));
	corona *= 1.0 - disc;
	// faded to nothing at the quad's edge
	corona *= 1.0 - smoothstep(reach * 0.6, reach * 0.95, a);
	vec3 core_colour = mix(vec3(1.0, 0.98, 0.93), vec3(1.0, 0.62, 0.32), warm);
	vec3 glow_colour = mix(vec3(1.0, 0.93, 0.80), vec3(1.0, 0.52, 0.24), warm);
	ALBEDO = (core_colour * disc * limb * 4.0 + glow_colour * corona) * brightness;
}

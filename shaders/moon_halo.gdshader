shader_type spatial;

// The moon's brightness (#1842, src/World/MoonDisc.cs): a glow round its true-size disc, its width a share of the
// screen as a star's glow is, its strength by the phase and the night, centred on the lit part. On a quad square to the
// moon's direction just inside the far plane, added onto the sky and over the disc (its depth a step nearer than the
// moon's: a crescent's dark disc ringed by glow read as an eclipse); a ridge or a building still hides it.
render_mode unshaded, fog_disabled, blend_add, depth_draw_never, cull_disabled, shadows_disabled;

// the disc's angular radius, rad
uniform float disc_r = 0.0045;
// the quad's angular half-size, rad
uniform float reach = 0.1;
// the inner falloff, rad (the outer one is four times it)
uniform float width = 0.005;
// the peak, added onto the sky
uniform float strength = 0.6;
// the glow's centre off the disc's, rad on the quad (toward the lit side)
uniform vec2 centre = vec2(0.0);
uniform vec3 colour = vec3(0.82, 0.87, 1.0);

#include "res://shaders/common/sky_depth.gdshaderinc"

void vertex() {
	POSITION = sky_depth(PROJECTION_MATRIX * (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)));
	// MoonDisc stands the quad in front of the disc's nearest point (#1845: a step in clip z near the far plane is a
	// tenth of that in distance, and the disc's unlit part hid the glow); the step keeps it over the disc at the floor
	POSITION.z = max(POSITION.z * 1.01, POSITION.w * SKY_MIN_DEPTH * 3.0);
}

void fragment() {
	vec2 t = vec2(UV.x * 2.0 - 1.0, 1.0 - UV.y * 2.0) * tan(reach);
	float a = length(t);
	// from the lit part's centre, the disc's lit share as its own small core
	float d = max(length(t - centre) - disc_r * 0.5, 0.0);
	float g = 0.55 * exp(-d / width) + 0.45 * exp(-d / (width * 4.0));
	g *= 1.0 - smoothstep(reach * 0.6, reach * 0.95, a);
	ALBEDO = colour * g * strength;
}

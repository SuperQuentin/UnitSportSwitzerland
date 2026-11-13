// The sun's look (#1479), one for the ground (shaders/sun.gdshader, World/SunDisc) and for space
// (shaders/atmosphere.gdshader): a limb-darkened core, a corona that widens and warms through more air, and
// diffraction spikes (a camera's starburst) along the screen's axes and, fainter, its diagonals.
//
// (parameters prefixed sl_: a shader including this has uniforms named core_r, warm)
// p: the offset from the sun's centre on the screen's axes, radians (x right, y up)
// core_r: the disc's angular radius, rad
// warm: 0 high in a clear sky (or above the air), 1 on the horizon
// spikes: the starburst's strength, faint on the ground, full in space
// aa: about a pixel, rad (the disc's edge)

const vec3 SUN_WHITE = vec3(1.0, 0.97, 0.90);
const vec3 SUN_LOW = vec3(1.0, 0.55, 0.22);

// the disc's light against the scene's 1: white hot high up, deep orange (and dimmer, so it is not clipped
// to white) low through the air
vec3 sun_core_colour(float sl_air) {
	return mix(SUN_WHITE * 3.0, SUN_LOW * 1.25, sl_air);
}

vec3 sun_look(vec2 p, float sl_core, float sl_air, float sl_spikes, float sl_aa) {
	float a = length(p);
	// the disc, limb-darkened (its edge ~60 % of its middle)
	float r = clamp(a / sl_core, 0.0, 1.0);
	float limb = 1.0 - 0.4 * (1.0 - sqrt(max(1.0 - r * r, 0.0)));
	float disc = 1.0 - smoothstep(sl_core - sl_aa, sl_core + sl_aa, a);
	// the corona: a bright halo close in and a wide faint glow, both wider through more air
	float out_r = max(a - sl_core, 0.0);
	float spread = 1.0 + 1.5 * sl_air;
	float corona = 0.45 * exp(-out_r / (0.010 * spread)) + 0.12 * exp(-out_r / (0.055 * spread));
	// through thick air the sky round it is already bright: less added, so the orange disc stands out
	corona *= mix(1.0, 0.5, sl_air);
	// the starburst: thin rays along the screen's axes, fainter along the diagonals, sized by the disc
	vec2 q = abs(p);
	vec2 d = abs(vec2(p.x + p.y, p.x - p.y)) * 0.70710678;
	float w = sl_core * 0.12, len = sl_core * 14.0;
	float burst = exp(-q.y / w - q.x / len) + exp(-q.x / w - q.y / len)
		+ 0.45 * (exp(-d.y / w - d.x / (len * 0.6)) + exp(-d.x / w - d.y / (len * 0.6)));
	burst *= sl_spikes * smoothstep(sl_core * 0.8, sl_core * 1.6, a);
	vec3 glow = mix(vec3(1.0, 0.93, 0.80), vec3(1.0, 0.50, 0.22), sl_air);
	return sun_core_colour(sl_air) * disc * limb + glow * (corona + 0.8 * burst) * (1.0 - disc);
}

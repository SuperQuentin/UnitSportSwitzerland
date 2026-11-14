// Light shafts from the sun (#1479, World/SunDisc): each pixel marches toward the sun's place on the screen over
// the depth buffer and counts how much of the way is open sky (nothing drawn: reversed Z, 0). Where a ridge, a
// tree or a building stands between, the count drops, so they cut beams through the glow; over open sky it is a
// soft halo. Added light, strongest with the sun low (more air to light up), none above the air, faded as the sun
// leaves the screen. One quad laid over the whole screen; the sun's place is worked out per view (VR's two eyes).
// Off in the settings ("Sun rays"), and by default on phones and standalone headsets.
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, depth_test_disabled, cull_disabled, fog_disabled, shadows_disabled;

uniform sampler2D depth_tex : hint_depth_texture, filter_nearest;
// toward the sun, world axes
uniform vec3 sun_dir = vec3(0.0, 1.0, 0.0);
// the shafts' light: their colour times strength (0 hides them: World/SunDisc then hides the node too)
uniform vec3 shaft_colour = vec3(1.0, 0.8, 0.55);
// 0 high in the sky, 1 on the horizon
uniform float warm = 0.0;

const int SAMPLES = 28;
// how far toward the sun each pixel looks, as a share of the way
const float REACH = 0.75;

void vertex() {
	POSITION = vec4(VERTEX.xy, 0.5, 1.0);
}

float ign(vec2 p) {
	return fract(52.9829189 * fract(dot(p, vec2(0.06711056, 0.00583715))));
}

void fragment() {
	// the sun's place on this view's screen
	vec4 s = PROJECTION_MATRIX * (VIEW_MATRIX * vec4(sun_dir, 0.0));
	if (s.w <= 0.0) discard;
	vec2 sun_uv = s.xy / s.w * 0.5 + 0.5;
	sun_uv.y = 1.0 - sun_uv.y;
	// faded as the sun leaves the screen (a little past its edge still lights the shafts)
	vec2 outside = max(abs(sun_uv - 0.5) - 0.5, 0.0);
	float on_screen = 1.0 - smoothstep(0.0, 0.35, length(outside));
	if (on_screen <= 0.0) discard;

	vec2 step_uv = (sun_uv - SCREEN_UV) * REACH / float(SAMPLES);
	vec2 uv = SCREEN_UV + step_uv * ign(FRAGCOORD.xy);
	float open = 0.0, weight = 1.0, total = 0.0;
	for (int i = 0; i < SAMPLES; i++) {
		if (uv.x >= 0.0 && uv.x <= 1.0 && uv.y >= 0.0 && uv.y <= 1.0)
			open += (texture(depth_tex, uv).x <= 1e-7 ? 1.0 : 0.0) * weight;
		total += weight;
		weight *= 0.95;
		uv += step_uv;
	}
	open /= total;
	// brighter close to the sun, and only where the way is mostly open
	vec2 to_sun = (SCREEN_UV - sun_uv) * vec2(VIEWPORT_SIZE.x / VIEWPORT_SIZE.y, 1.0);
	float near = exp(-length(to_sun) * mix(4.5, 2.2, warm));
	ALBEDO = shaft_colour * open * open * near * on_screen + (ign(FRAGCOORD.yx + 5.0) - 0.5) / 255.0;
}

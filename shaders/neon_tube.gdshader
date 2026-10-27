// A neon tube (#1232, Interiors/NeonSign): unshaded and brighter than white, so the lit styles' glow
// (HDR threshold 1.2) haloes it. The colour is in the vertex colour, the tube's brightness across
// its width in UV.y (0 at an edge, 1 on the core): a white-hot core fading to the gas colour.
// An optional flicker drops a whole sign for a moment now and then, keyed on its position, so
// signs never flicker together. Nothing is set per frame.
shader_type spatial;
render_mode unshaded, cull_disabled, shadows_disabled;

uniform float energy = 1.6;
uniform float flicker = 0.0;

void fragment() {
	float core = smoothstep(0.35, 1.0, UV.y);
	vec3 gas = COLOR.rgb;
	vec3 c = mix(gas, mix(gas, vec3(1.0), 0.5), core) * energy;
	if (flicker > 0.0) {
		float t = floor(TIME * 9.0 + dot(NODE_POSITION_WORLD, vec3(0.37, 0.0, 0.71)));
		float r = fract(sin(t * 12.9898) * 43758.5453);
		c *= 1.0 - flicker * step(0.965, r);
	}
	ALBEDO = c;
}

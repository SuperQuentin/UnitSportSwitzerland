// Saturn's ring ice chunks (#1854, src/World/Bodies/RingChunks.cs): lit by the sun alone (Lambert, no sky to fill
// them, a little ring-shine on the dark side), each chunk its own tint (the MultiMesh's colour: bright water ice, some
// dusty). Unshaded like the globes, so the chunks and the slab round them read in one light.
shader_type spatial;
render_mode unshaded, fog_disabled, cull_disabled;

// world axes, unit, toward the sun
uniform vec3 sun_dir = vec3(0.0, 1.0, 0.0);
// the rings' own glow on a chunk's night side
uniform float ring_shine = 0.12;
uniform float sun_strength = 1.3;

varying vec3 world_normal;
varying vec3 tint;

void vertex() {
	world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
	tint = COLOR.rgb;
}

void fragment() {
	float lambert = max(dot(normalize(world_normal), normalize(sun_dir)), 0.0);
	ALBEDO = tint * (ring_shine + sun_strength * lambert);
}

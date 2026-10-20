// The concert crowd (#885): thousands of low-poly people in one MultiMesh, drawn here.
// INSTANCE_CUSTOM: x = the person's own phase (0..1), yzw = their shirt colour.
// Vertex colour: the body part's colour, alpha 1 where the shirt is.
// They bounce on the beat as hard as the show asks (energy), sway a little, and catch the
// rig's light (tint): the crowd flashes with the show, with no light of its own.
shader_type spatial;
render_mode unshaded, cull_disabled, fog_disabled;

uniform float beat = 0.0;
uniform float energy = 0.0;
uniform vec3 tint : source_color = vec3(0.2);
uniform float arms_up = 0.0;

varying vec3 shirt;
varying float flash;

void vertex() {
	float seed = INSTANCE_CUSTOM.x;
	shirt = INSTANCE_CUSTOM.yzw;
	// a hop on every beat, some on the off-beat: a crowd is never quite together
	float ph = beat + (seed > 0.8 ? 0.5 : seed * 0.15);
	float hop = energy * 0.16 * pow(abs(sin(3.14159 * ph)), 3.0);
	float sway = sin((beat * 0.5 + seed) * 6.2832) * 0.06 * (0.3 + energy) * VERTEX.y;
	VERTEX.x += sway;
	VERTEX.y += hop;
	// hands in the air: the shirt's top corners rise in a chorus
	if (COLOR.a > 0.5 && VERTEX.y > 1.3 && arms_up > 0.0) VERTEX.y += arms_up * 0.45 * step(0.5, fract(seed * 7.0));
	flash = seed;
}

void fragment() {
	vec3 base = mix(COLOR.rgb, shirt, COLOR.a);
	ALBEDO = base * (0.07 + tint);
}

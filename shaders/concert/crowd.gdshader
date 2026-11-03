// The concert crowd (#885): thousands of low-poly people in one MultiMesh, drawn here.
// INSTANCE_CUSTOM: x = the person's own phase (0..1), yzw = their shirt colour.
// Vertex colour: the body part's colour, alpha 1 where the shirt is, 0.25 on the wristband.
// They bounce on the beat as hard as the show asks (energy), sway a little, and catch the
// rig's light (tint): the crowd flashes with the show, with no light of its own.
// The wristbands (#986): each band tests its person against the IR emitters' cones (ir, world
// frame, Wristbands.cs) and lights in the colour of the strongest that covers it, or in the
// transmitter's (bands_all) when that is brighter. Only the band's vertices pay for the test.
shader_type spatial;
render_mode unshaded, cull_disabled, fog_disabled;

uniform float beat = 0.0;
uniform float energy = 0.0;
uniform vec3 tint : source_color = vec3(0.2);
uniform float arms_up = 0.0;
// a seated crowd (a jazz night's lawn, #1394): 1 lowers the person 0.45 m, the legs folded under; 0 stands
uniform float seated = 0.0;

// four per emitter: (position, cos half angle), (beam now), (beam a moment ago), (colour, power)
uniform vec4 ir[64];
uniform int ir_count = 0;
// the transmitter: colour and level; its effect (0 solid, 1 twinkle, 2 beat flash) and rate
uniform vec4 bands_all = vec4(0.0);
uniform vec2 bands_effect = vec2(0.0);

varying vec3 shirt;
varying float flash;
varying vec4 band;

float hash(float x) {
	return fract(sin(x * 127.1) * 43758.5453);
}

void vertex() {
	float seed = INSTANCE_CUSTOM.x;
	shirt = INSTANCE_CUSTOM.yzw;
	band = vec4(0.0);
	bool is_band = COLOR.a > 0.1 && COLOR.a < 0.4;
	float raised = arms_up * step(0.5, fract(seed * 7.0));
	if (is_band) {
		// the person's wrist in the world (the instance's own transform is in MODEL_MATRIX)
		vec3 p = (MODEL_MATRIX * vec4(0.27, 0.84 + raised, 0.0, 1.0)).xyz;
		vec3 lit = vec3(0.0);
		float best = 0.0;
		for (int k = 0; k < ir_count; k++) {
			vec4 a = ir[k * 4];
			vec3 n = normalize(p - a.xyz);
			// the beam's sweep from a moment ago to now: the nearest of its directions
			vec3 b0 = ir[k * 4 + 2].xyz, b1 = ir[k * 4 + 1].xyz, e = b1 - b0;
			float t = clamp(dot(n - b0, e) / max(dot(e, e), 1e-6), 0.0, 1.0);
			float c = dot(n, normalize(mix(b0, b1, t)));
			vec4 col = ir[k * 4 + 3];
			float s = col.a * smoothstep(a.w, mix(a.w, 1.0, 0.12), c);
			if (s > best) { best = s; lit = col.rgb; }
		}
		// every band at once: solid, a random twinkle, or a random half on each beat
		float all = bands_all.a;
		if (bands_effect.x > 1.5) {
			all *= step(0.5, hash(seed * 53.0 + floor(beat))) * exp(-fract(beat) * 2.5);
		} else if (bands_effect.x > 0.5) {
			float tw = TIME * (0.15 + 2.0 * bands_effect.y) + seed * 17.0;
			all *= step(0.6, hash(seed * 31.0 + floor(tw))) * sin(3.14159 * fract(tw));
		}
		if (all > best) { best = all; lit = bands_all.rgb; }
		band = vec4(lit * best, 1.0);
		// a lit LED reads bigger than its plastic: the band swells with its light
		float glow = max(lit.r, max(lit.g, lit.b)) * best;
		VERTEX = vec3(0.27, 0.84, 0.0) + (VERTEX - vec3(0.27, 0.84, 0.0)) * (1.0 + 0.8 * glow);
		// up with the hand in a chorus
		VERTEX.y += raised * 1.0;
		VERTEX.x -= raised * 0.04;
	}
	if (seated > 0.0) {
		if (VERTEX.y > 0.8) VERTEX.y -= 0.45 * seated;
		else VERTEX.y *= 1.0 - 0.5625 * seated;
	}
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
	// a band is its own light: the rig's colour does not wash it
	if (band.a > 0.5) ALBEDO = COLOR.rgb * 0.5 + band.rgb * 1.5;
}

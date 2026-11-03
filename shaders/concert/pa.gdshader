// A concert's PA (#885, ConcertPa): every cabinet and woofer baked in one mesh, bounced here on the
// kick. CUSTOM0.xyz: the element's pivot (model space); CUSTOM0.w: 0 an array cabinet, 1 a sub
// cabinet, 2 an array woofer, 3 a sub woofer. CUSTOM1.xyz: the way the element faces.
shader_type spatial;
render_mode cull_back;

uniform float kick = 0.0;
uniform float bass = 0.6;

void vertex() {
	vec3 pivot = CUSTOM0.xyz;
	float kind = CUSTOM0.w;
	bool sub = kind > 0.5 && kind < 1.5 || kind > 2.5;
	float hit = sub ? min(1.0, kick * (0.6 + 0.6 * bass)) : kick;
	if (kind < 1.5) {
		// the cabinet swells a little on the hit
		VERTEX = pivot + (VERTEX - pivot) * (1.0 + (sub ? 0.035 : 0.012) * hit);
	} else {
		// the cone pushes out and grows
		VERTEX = pivot + (VERTEX - pivot) * (1.0 + 0.1 * hit) + CUSTOM1.xyz * (sub ? 0.06 : 0.025) * hit;
	}
}

void fragment() {
	ALBEDO = COLOR.rgb;
	ROUGHNESS = 0.75;
}

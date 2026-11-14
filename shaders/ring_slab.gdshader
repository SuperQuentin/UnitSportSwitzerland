// Saturn's rings as a slab (#1854, src/World/Bodies/BodyGlobe.cs): one quad over the whole screen, each pixel shading
// its view ray's path through the slab round the equator, found analytically (faces at the slab's place were within the
// near plane whenever the camera was in or by the shrunk slab). The path, in a few steps, each step's length over the
// thickness times the ring's normal optical depth there (the ring map's alpha) gives how much the ring hides: the familiar
// ring face-on, a band of its true thickness edge-on, a haze when the camera is inside it. Everything is measured from
// the camera (its height over the mid-plane comes in from doubles): Saturn's system is shrunk toward the camera past the
// far plane, and centre-relative floats were 8 m out. Saturn's shadow darkens the part behind it from the sun; the part
// behind Saturn from the camera is hidden; what stands in front (an ice chunk, Saturn) ends the path.
shader_type spatial;
render_mode unshaded, fog_disabled, blend_mix, depth_draw_never, depth_test_disabled, cull_disabled, shadows_disabled;

uniform sampler2D rings : source_color, filter_linear_mipmap, repeat_disable;
uniform sampler2D depth_tex : hint_depth_texture, filter_nearest;
// the inner edge over the outer
uniform float inner = 0.53;
// world units (the system's shrink applied): the outer radius, the half thickness, the camera over the mid-plane
uniform float outer_world = 1.0;
uniform float half_thick = 0.001;
uniform float cam_height = 0.0;
// the system's shrink toward the camera: a true distance times it is a world one
uniform float shrink = 1.0;
// what the depth buffer holds nearer than this (world units) is in true scale round the camera (the ice chunks, a craft);
// farther, it is Saturn's shrunk system (its globe), already in the slab's units
uniform float true_reach = 3000.0;
// Saturn's centre and pole (world), its radius (world), toward the sun (world, unit)
uniform vec3 centre;
uniform vec3 pole = vec3(0.0, 0.0, 1.0);
uniform float planet_r = 0.5;
uniform vec3 sun_dir = vec3(0.0, 1.0, 0.0);
// the normal optical depth where the map's alpha is 1 (the B ring's ~2)
uniform float tau = 2.0;
uniform float brightness = 1.0;

void vertex() {
	// the quad's corners (a 2 m QuadMesh, ±1) straight onto the screen's, as the atmosphere's
	POSITION = vec4(VERTEX.xy, 0.5, 1.0);
}

void fragment() {
	vec3 cam = CAMERA_POSITION_WORLD;
	vec4 ray = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, 1.0, 1.0);
	vec3 dir = normalize((INV_VIEW_MATRIX * vec4(ray.xyz / ray.w, 0.0)).xyz);
	bool inside = abs(cam_height) < half_thick;
	float dn = dot(dir, pole);
	float t0 = 0.0, t1 = 1e12;
	if (abs(dn) > 1e-7) {
		float a = (-half_thick - cam_height) / dn, b = (half_thick - cam_height) / dn;
		t0 = max(min(a, b), 0.0);
		t1 = max(a, b);
	} else if (!inside) discard;
	// what stands in front ends the path: a chunk near the camera (true scale, brought to the shrunk system's units),
	// or Saturn behind (already shrunk)
	float depth = texture(depth_tex, SCREEN_UV).x;
	if (depth > 1e-7) {
		vec4 at = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, depth, 1.0);
		at.xyz /= at.w;
		float scene = length(at.xyz);
		t1 = min(t1, scene < true_reach ? scene * shrink : scene);
	}
	// within the ring's outer edge (a cylinder round the pole): from beyond it, edge-on, the band has its true height
	vec3 oc = cam - centre;
	vec3 o = oc - pole * dot(oc, pole);
	vec3 d2 = dir - pole * dn;
	float qa = dot(d2, d2);
	if (qa > 1e-10) {
		float lo = length(o);
		float qb = dot(o, d2), qc = (lo - outer_world) * (lo + outer_world);
		float disc = qb * qb - qa * qc;
		if (disc < 0.0) discard;
		float sq = sqrt(disc);
		t0 = max(t0, (-qb - sq) / qa);
		t1 = min(t1, (-qb + sq) / qa);
	}
	// a ray along the plane from inside: no further than a couple of thousand thicknesses (opaque long before)
	t1 = min(t1, t0 + 2000.0 * half_thick);
	if (t1 <= t0) discard;
	// the path in a few steps, each its own radius: a gap, the inner edge or the other ring crossed on the way is kept
	const int STEPS = 10;
	float dt = (t1 - t0) / float(STEPS);
	vec3 to_c = centre - cam;
	float lc = length(to_c);
	vec3 nc = to_c / lc;
	vec3 s = normalize(sun_dir);
	float lit0 = 0.35 + 0.65 * abs(dot(s, pole));
	// one mip level for the whole path, from how much radius a pixel spans at its middle
	vec3 dm = cam + dir * (0.5 * (t0 + t1)) - centre;
	float rm = length(dm - pole * dot(dm, pole)) / outer_world;
	float lod = log2(max(fwidth(rm) * float(textureSize(rings, 0).x) / (1.0 - inner), 1.0));
	float trans = 1.0;
	vec3 col = vec3(0.0);
	for (int i = 0; i < STEPS; i++) {
		vec3 p = cam + dir * (t0 + (float(i) + 0.5) * dt);
		vec3 d = p - centre;
		float r = length(d - pole * dot(d, pole)) / outer_world;
		if (r < inner || r > 1.0) continue;
		// behind Saturn from the camera: hidden
		float along_c = dot(p - cam, nc);
		if (along_c > lc && length((p - cam) * (lc / along_c) - to_c) < planet_r) continue;
		vec4 c = textureLod(rings, vec2((r - inner) / (1.0 - inner), 0.5), lod);
		float a = 1.0 - exp(-tau * c.a * dt / (2.0 * half_thick));
		// lit by the sun's height over the plane; in Saturn's shadow nearly dark
		float lit = lit0;
		float along_s = dot(d, s);
		if (along_s < 0.0 && length(d - s * along_s) < planet_r) lit *= 0.08;
		col += trans * a * c.rgb * lit;
		trans *= 1.0 - a;
	}
	float alpha = 1.0 - trans;
	if (alpha < 1e-3) discard;
	ALBEDO = col / alpha * brightness;
	ALPHA = alpha;
}

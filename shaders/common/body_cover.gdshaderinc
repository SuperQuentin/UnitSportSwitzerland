// What covers another body's ground below its data (#2016, src/Terrain/Bodies/BodyCover.cs, World/Bodies/BodyCoverLook.cs):
// the colour of its cover classes over the photo's. Included by the near ground (body_ground.gdshader: its squares carry
// per vertex the fresh craters' rays and the dune sand, which only they have the relief for) and the anchor body's globe
// tiles (body_globe.gdshaderinc), so what the shader makes on its own (the regolith's mottling, Mars' dust, Io's sulphur,
// the polar ice by latitude and season) is the same on both and never ends at a square's edge.
//
// Body-fixed places, precise near the camera: cover_basis turns a world offset into the body's axes, from cover_origin, a
// point of the body's 4096 m lattice near the camera (in world space, written when the camera crosses a lattice cell or the
// world re-anchors). The noise repeats every 4096 m, so whichever lattice point is the origin, a place gets the same colour.

// 0 none (the Earth, a body seen from afar); else BodyCover.Kind: 1 regolith, 2 Mars, 3 ice, 4 sulphur, 5 basalt, 6 Titan
uniform int cover_kind = 0;
uniform vec3 cover_origin = vec3(0.0);
uniform mat3 cover_basis = mat3(1.0);
// the body's centre and spin axis (north) in world space
uniform vec3 cover_centre = vec3(0.0, -1.0e6, 0.0);
uniform vec3 cover_pole = vec3(0.0, 1.0, 0.0);
// where the polar ice ends, degrees of latitude, north and south (90: none; BodyCover.IceEdges, by the season)
uniform vec2 ice_edges = vec2(90.0, 90.0);
// the body's radius, metres (the ice edge's raggedness at a fixed size on the ground)
uniform float cover_radius = 1.0e6;

const float COVER_PERIOD = 4096.0;

uint cover_hash(uvec3 v) {
	v = v * 1664525u + 1013904223u;
	v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
	v ^= v >> 16u;
	v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
	return v.x ^ v.y ^ v.z;
}

// value noise, -1..1, on cells of `cell` metres, repeating every COVER_PERIOD (cell divides it)
float cover_noise(vec3 p, float cell) {
	vec3 q = p / cell;
	vec3 i = floor(q);
	vec3 f = q - i;
	vec3 u = f * f * (3.0 - 2.0 * f);
	float n = COVER_PERIOD / cell;
	float sum = 0.0;
	for (int c = 0; c < 8; c++) {
		vec3 o = vec3(float(c & 1), float((c >> 1) & 1), float((c >> 2) & 1));
		vec3 k = mod(i + o, n);
		float h = float(cover_hash(uvec3(k + n))) / 4294967295.0 * 2.0 - 1.0;
		vec3 w = mix(1.0 - u, u, o);
		sum += h * w.x * w.y * w.z;
	}
	return sum;
}

// plain value noise on a direction (no period), for kilometre features over the whole body
float cover_far_noise(vec3 p) {
	vec3 i = floor(p);
	vec3 f = p - i;
	vec3 u = f * f * (3.0 - 2.0 * f);
	float sum = 0.0;
	for (int c = 0; c < 8; c++) {
		vec3 o = vec3(float(c & 1), float((c >> 1) & 1), float((c >> 2) & 1));
		ivec3 k = ivec3(i + o);
		float h = float(cover_hash(uvec3(k + 1048576))) / 4294967295.0 * 2.0 - 1.0;
		vec3 w = mix(1.0 - u, u, o);
		sum += h * w.x * w.y * w.z;
	}
	return sum;
}

// how much of an octave of `cell` metres shows at `dist` metres: faded out before it shimmers
float cover_octave(float cell, float dist) {
	return 1.0 - smoothstep(cell * 60.0, cell * 200.0, dist);
}

float cover_luma(vec3 c) {
	return dot(c, vec3(0.2126, 0.7152, 0.0722));
}

// The cover over `col` (the photo's colour, lifted) at a world point. rays and sand: the square's per-vertex shares (0 on
// the globe's tiles); dist: from the camera.
vec3 cover_apply(vec3 col, vec3 wpos, vec3 wnormal, float rays, float sand, float dist) {
	if (cover_kind == 0) return col;
	// the squares' own shares fade out well before the medium square's edge (1.0-1.5 km, its geomorph): what lies past it
	// is the photo's, and a dark dune field ending there drew a band on the horizon
	float near_share = 1.0 - smoothstep(400.0, 1100.0, dist);
	rays *= near_share;
	sand *= near_share;
	vec3 b = cover_basis * (wpos - cover_origin);
	// the fine mottling: a pebble's, a clod's, a ripple's scale, faded with the distance
	float o0 = cover_octave(0.5, dist), o1 = cover_octave(2.0, dist), o2 = cover_octave(8.0, dist), o3 = cover_octave(64.0, dist);
	float fine = (o0 > 0.0 ? 0.35 * o0 * cover_noise(b, 0.5) : 0.0)
		+ (o1 > 0.0 ? 0.35 * o1 * cover_noise(b, 2.0) : 0.0)
		+ (o2 > 0.0 ? 0.3 * o2 * cover_noise(b, 8.0) : 0.0);
	float broad = (o3 > 0.0 ? 0.6 * o3 * cover_noise(b, 64.0) : 0.0) + 0.4 * cover_octave(512.0, dist) * cover_noise(b, 512.0);

	if (cover_kind == 1) {
		// regolith: the photo's grey carried down, mottled; fresh ejecta and rays bright (immature soil, ~1.8 the albedo)
		col *= 1.0 + 0.12 * fine + 0.06 * broad;
		col = mix(col, min(col * 1.9 + 0.03, vec3(1.0)), rays * 0.6);
	} else if (cover_kind == 2) {
		// Mars: ochre dust in drifts, dark basalt sand in the dune fields, fresh craters' dark bluish blast zones
		float dust = smoothstep(-0.25, 0.45, broad + 0.4 * fine);
		col = mix(col, col * vec3(1.16, 1.08, 0.92), dust * 0.55);
		col *= 1.0 + 0.1 * fine;
		col = mix(col, col * vec3(0.56, 0.53, 0.55), sand * 0.7);
		col = mix(col, col * vec3(0.72, 0.76, 0.86), rays * 0.45);
	} else if (cover_kind == 3) {
		// water ice: fresh ice bright and bluer round young craters, the old ice dusted
		col *= 1.0 + 0.07 * fine + 0.05 * broad;
		col = mix(col, min(col * 1.5 + vec3(0.06, 0.08, 0.1), vec3(1.0)), rays * 0.5);
	} else if (cover_kind == 4) {
		// Io: sulphur's yellows, its pale SO₂ frost, red short-chain sulphur, black spots, in patches over the photo's own
		float s = broad + 0.3 * fine;
		vec3 pal = s < -0.25 ? mix(vec3(0.62, 0.30, 0.14), vec3(0.85, 0.74, 0.30), smoothstep(-0.5, -0.25, s))
			: s < 0.3 ? mix(vec3(0.85, 0.74, 0.30), vec3(0.88, 0.86, 0.72), smoothstep(-0.25, 0.3, s))
			: vec3(0.88, 0.86, 0.72);
		float luma = cover_luma(col);
		col = mix(col, pal * (0.6 + 0.6 * luma), 0.35);
		col *= 1.0 - 0.7 * smoothstep(0.55, 0.75, fine);
		col = mix(col, min(col * 1.4, vec3(1.0)), rays * 0.4);
	} else if (cover_kind == 5) {
		// Venus: basalt flows of different ages, the fresh darker
		col *= 0.92 + 0.12 * broad + 0.08 * fine;
		col = mix(col, col * 1.3, rays * 0.4);
	} else if (cover_kind == 6) {
		// Titan: dark organic sand in the dune seas, pale ice pebble plains
		col *= 1.0 + 0.08 * fine + 0.05 * broad;
		col = mix(col, col * vec3(0.58, 0.52, 0.46), sand * 0.7);
		col = mix(col, min(col * 1.35, vec3(1.0)), rays * 0.4);
	}

	// the polar ice by latitude (and season, in ice_edges): further toward the equator on slopes facing the pole, the edge
	// ragged at tens of kilometres
	if (ice_edges.x < 90.0 || ice_edges.y < 90.0) {
		vec3 up = normalize(wpos - cover_centre);
		float lat = degrees(asin(clamp(dot(up, cover_pole), -1.0, 1.0)));
		vec3 n = normalize(wnormal);
		vec3 tilt = n - up * dot(n, up);
		float poleward = dot(tilt, normalize(cover_pole - up * dot(cover_pole, up))) * sign(lat);
		float ragged = 2.5 * cover_far_noise(up * cover_radius / 40000.0) + 1.0 * cover_far_noise(up * cover_radius / 8000.0)
			+ 0.6 * broad + 8.0 * poleward;
		float a = abs(lat) + ragged;
		float edge = lat >= 0.0 ? ice_edges.x : ice_edges.y;
		float ice = smoothstep(edge - 1.0, edge + 1.0, a);
		// frost thin at the edge, solid further in; dust-tinted CO₂ frost on Mars, white on Ganymede
		vec3 frost = cover_kind == 2 ? vec3(0.92, 0.89, 0.86) : vec3(0.9, 0.92, 0.95);
		col = mix(col, frost * (0.92 + 0.08 * fine), ice * (0.55 + 0.4 * smoothstep(edge + 1.0, edge + 6.0, a)));
	}
	return col;
}

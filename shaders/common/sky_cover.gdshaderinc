// What stands in for a far body in the sky (#2051): another body's globe shrunk about the camera, the Moon's disc, a
// planet's disc. Each is drawn after the stars (a render priority above theirs), covering them where the sky is dark,
// and steps aside wherever the scene's opaque geometry is in front of it. Depth cannot tell either: high up the view is
// 1 : 10 million from near to far, and a stand-in at 85 % of the far plane had a depth under the 24-bit buffer's step
// (6e-8), under the stars' pinned 1.5e-7: the stars were drawn through the Earth ("semitransparent, the stars seen
// through it"), and a ridge past 85 % of a short view behind it ("the earth draw in front of the far terrain").
//
// The scene's depth texture holds only opaque geometry. Every opaque thing is nearer than any far body but the anchor's
// own body (its globe, drawn at its true place), which a far body passes in front of (the Moon across the Earth from
// beyond it): there the far body's true distance against the anchor's sphere along the pixel decides. Where the depth is
// empty (nothing drawn, or ground so near the far plane that its depth rounds to nothing), the anchor's bulk still hides
// what is behind it (world_anchor_sphere, World/Globe/Atmosphere).

uniform sampler2D sky_cover_depth : hint_depth_texture, filter_nearest;

// the anchor's body: its centre (world) and a radius a little under its least (the camera stays outside it); w 0: none
global uniform vec4 world_anchor_sphere;

// a ray's entry and exit of a sphere round the origin, (-1, -1) when it misses
vec2 sky_cover_sphere(vec3 o, vec3 d, float r) {
	float b = dot(o, d);
	float c = dot(o, o) - r * r;
	float disc = b * b - c;
	if (disc < 0.0) return vec2(-1.0, -1.0);
	float s = sqrt(disc);
	return vec2(-b - s, -b + s);
}

// true when the far body at this pixel (true_dist metres from the camera along dir, world axes) is hidden by the scene
bool sky_covered(vec2 uv, vec3 cam, vec3 dir, float true_dist, mat4 inv_proj) {
	float depth = texture(sky_cover_depth, uv).r;
	vec2 hit = world_anchor_sphere.w > 0.0 ? sky_cover_sphere(cam - world_anchor_sphere.xyz, dir, world_anchor_sphere.w) : vec2(-1.0);
	bool anchor_first = hit.x > 0.0 && hit.x < true_dist;
	// reversed depth: 0 where nothing opaque was drawn
	if (depth <= 0.0) return anchor_first;
	// something opaque: in front of the far body, unless it is the anchor's own surface past it (a transit)
	if (hit.x > 0.0 && true_dist < hit.x) {
		vec4 at = inv_proj * vec4(uv * 2.0 - 1.0, depth, 1.0);
		float scene = length(at.xyz / at.w);
		if (scene >= hit.x * 0.98) return false;
	}
	return true;
}

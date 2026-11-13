// What is drawn in the sky just inside the far plane (stars, planets, the moon, satellites) keeps its depth a
// few steps of the depth buffer above the far plane's (#1518). Depth is reversed: 0 at the far plane. High up the
// camera sees 1 : 3 million from near to far (0.33 m to 1,117 km at 66 km), and at 95 % of the far plane a star
// straight ahead fell under a step of the 24-bit buffer (6e-8): it read as the far plane itself and failed the
// test. Off the view's axis its depth was larger, so the stars went in a disc round whatever was looked at.
// Ridges whose own depth is that close to the far plane (past ~62 km of a 70 km view) no longer hide them.
const float SKY_MIN_DEPTH = 1.5e-7;

vec4 sky_depth(vec4 clip) {
	clip.z = max(clip.z, clip.w * SKY_MIN_DEPTH);
	return clip;
}

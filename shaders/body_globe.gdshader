// Another body's globe (#1848, World/Bodies/BodyGlobe.cs): its pyramid's tiles in real geometry, lit by its own
// sun (the direction from the body to the Sun in world axes, set each frame), so the Moon's terminator is where the
// ephemeris puts it whatever the anchor's body. No air, no haze: the Moon's ground is as sharp at 300 km as at 3.
// The colour is the body's photo (the CGI Moon Kit's Hapke-normalised LROC colour: no shadows of its own), lifted
// for Cartoon as the Earth's globe lifts Blue Marble.
shader_type spatial;
render_mode unshaded, cull_disabled, fog_disabled;

// the anchor's own body (its tiles at their true place): opaque, in the depth everything else is tested against
#include "res://shaders/common/body_globe.gdshaderinc"

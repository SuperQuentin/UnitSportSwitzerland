// Another body's globe as a stand-in in the sky (#2051, World/Bodies/BodyGlobe.cs): shrunk about the camera past the far
// plane, drawn after the stars and covering them, stepping aside where the scene's opaque geometry is in front
// (shaders/common/sky_cover.gdshaderinc). The same look as body_globe.gdshader.
shader_type spatial;
render_mode unshaded, cull_disabled, fog_disabled, depth_draw_never, shadows_disabled;

#define SKY_STANDIN
#include "res://shaders/common/body_globe.gdshaderinc"

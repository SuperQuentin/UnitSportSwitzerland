shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_always;
// ps1_water without the scene's depth, for a standalone headset (#1112, Styles/StyleKit)
#define NO_SCENE_DEPTH
#include "res://shaders/body/water.gdshaderinc"

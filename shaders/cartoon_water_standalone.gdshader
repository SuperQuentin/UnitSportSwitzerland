shader_type spatial;
render_mode cull_disabled, depth_draw_always, shadows_disabled;
// cartoon_water without the scene's depth, for a standalone headset (#1112, Styles/StyleKit): opaque
#define STYLE_LIT
#define STYLE_CARTOON
#define NO_SCENE_DEPTH
#define NO_DISCARD
#include "res://shaders/body/water.gdshaderinc"

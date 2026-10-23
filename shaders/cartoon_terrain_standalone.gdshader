shader_type spatial;
render_mode cull_disabled;
// cartoon_terrain without discard, for a standalone headset (#1112, Styles/StyleKit)
#define STYLE_LIT
#define STYLE_CARTOON
#define NO_DISCARD
#define STANDALONE
// the occasions' snow and mist cost a Quest 2 about a millisecond even on a day without one
#define LITE_NO_OCCASION
#include "res://shaders/body/terrain.gdshaderinc"

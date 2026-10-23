shader_type spatial;
render_mode cull_disabled;
// cartoon_terrain without discard, for a standalone headset (#1112, Styles/StyleKit)
#define STYLE_LIT
#define STYLE_CARTOON
#define NO_DISCARD
#define STANDALONE
#include "res://shaders/body/terrain.gdshaderinc"

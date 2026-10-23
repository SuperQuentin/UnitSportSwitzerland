shader_type spatial;
render_mode cull_disabled, depth_draw_opaque;
// cartoon_road without discard, for a standalone headset (#1112, Styles/StyleKit): a discard turned a
// Quest's early depth rejection off, and a city's every hidden layer was shaded
#define NO_DISCARD
#define STANDALONE
#define STYLE_LIT
#define STYLE_CARTOON
#include "res://shaders/body/road.gdshaderinc"

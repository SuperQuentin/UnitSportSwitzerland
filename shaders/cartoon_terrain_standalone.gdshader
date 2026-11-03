shader_type spatial;
render_mode cull_back;
// cartoon_terrain without discard, for a standalone headset (#1112, Styles/StyleKit); back faces culled, as the
// ground's are all front (a cull_front preview drew no ground): a tiler set up and binned each triangle twice
#define STYLE_LIT
#define STYLE_CARTOON
#define NO_DISCARD
#define STANDALONE
// the occasions' snow and mist cost a Quest 2 about a millisecond even on a day without one
#define LITE_NO_OCCASION
// nor the doors' lamp light on the ground at night (~0.7 ms)
#define LITE_NO_EMISSION
#include "res://shaders/body/terrain.gdshaderinc"

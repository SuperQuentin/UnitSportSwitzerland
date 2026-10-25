shader_type spatial;
render_mode cull_disabled;
// cartoon_terrain without discard, for a standalone headset, --terrainvariant 1 (a cost check) (#1112, Styles/StyleKit)
#define STYLE_LIT
#define STYLE_CARTOON
#define NO_DISCARD
#define STANDALONE
#define LITE_NO_EMISSION
#include "res://shaders/body/terrain.gdshaderinc"

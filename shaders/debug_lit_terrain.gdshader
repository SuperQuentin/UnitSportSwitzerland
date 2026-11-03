shader_type spatial;
render_mode cull_disabled;
// --litterrain (#1112): the ground lit by the style's light() and nothing else, to split the terrain
// shader's cost between Godot's lighting and its own per-pixel code
#define STYLE_LIT
#define STYLE_CARTOON
#include "res://shaders/common/world.gdshaderinc"
#include "res://shaders/common/style.gdshaderinc"
void fragment() {
    vec3 n = facet_normal(VERTEX, INV_VIEW_MATRIX);
    if (n.y < 0.0) {
        n = -n;
    }
    ALBEDO = COLOR.rgb;
    NORMAL = style_view_normal(VIEW_MATRIX, n);
    ROUGHNESS = 1.0;
}

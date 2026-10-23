shader_type spatial;
render_mode unshaded, cull_disabled;
// --plainterrain (#1112): the cheapest ground there is, to measure what the terrain shader costs a headset
void fragment() {
    ALBEDO = COLOR.rgb;
}

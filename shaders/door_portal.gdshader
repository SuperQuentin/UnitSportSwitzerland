shader_type spatial;
render_mode unshaded, cull_disabled;

// An open doorway (Interiors/DoorPortals): what the portal camera sees on the other side of the
// door, drawn in screen space so the picture lines up with the view through the hole. The portal
// camera copies the view's projection exactly, which is what makes SCREEN_UV the right lookup.
// A doorway with no portal camera to spare shows a dark, unlit hall instead.

uniform sampler2D view : source_color, filter_nearest;
uniform bool live = false;
uniform vec3 dark : source_color = vec3(0.07, 0.06, 0.05);

void fragment() {
    ALBEDO = live ? texture(view, SCREEN_UV).rgb : dark;
}

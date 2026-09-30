shader_type spatial;
render_mode unshaded, cull_disabled;

// An open doorway (Interiors/DoorPortals): what a portal camera sees on the other side of the
// door, drawn in screen space so the picture lines up with the view through the hole. The portal
// camera copies the viewing camera's projection exactly, which is what makes SCREEN_UV the right
// lookup. A doorway with no portal camera to spare shows a dark, unlit hall instead.
//
// A doorway is only ever looked into from its own side. A camera behind it, however close, is a
// portal camera looking out through that very doorway from the other side, and must see through
// it, so the quad draws nothing for it. The screen's camera is never in the doorway itself
// (DoorPortals.KeepOutOfDoorways); a slack here for it made a lens a few cm in front of a doorway
// black: its portal camera, as close behind the far doorway, drew that one's dark hall.

#include "res://shaders/portal_clip.gdshaderinc"

uniform sampler2D view : source_color, filter_nearest;
uniform bool live = false;
uniform vec3 dark : source_color = vec3(0.07, 0.06, 0.05);
// the quad's mouth: xyz toward the side it is seen from, w = dot(normal, a point on it)
uniform vec4 facing = vec4(0.0, 0.0, 0.0, -1.0);

varying vec3 world_pos;

void vertex() {
    world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
    // a doorway between a portal camera and the doorway it looks through
    if (portal_clipped(world_pos, CAMERA_POSITION_WORLD)) {
        discard;
    }
    if (dot(CAMERA_POSITION_WORLD, facing.xyz) < facing.w) {
        discard;
    }
    ALBEDO = live ? texture(view, SCREEN_UV).rgb : dark;
}

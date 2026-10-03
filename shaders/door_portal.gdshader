shader_type spatial;
render_mode unshaded, cull_disabled, fog_disabled;
// fog_disabled: the portal camera's picture is already fogged; fogging the quad again made the
// room seen through a doorway hazier than the same room seen from inside

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

// The portal's picture is an HDR viewport (#388): linear already, not tonemapped (the screen's
// camera tonemaps this quad), at the portal environment's exposure, which view_gain undoes.
uniform sampler2D view : filter_nearest;
// In a headset (#244): one picture per eye, each from that eye's own frustum; `view` is the left.
uniform sampler2D view_right : filter_nearest;
uniform float view_gain = 1.0;
uniform bool stereo = false;
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
    if (!live) {
        ALBEDO = dark;
    } else if (stereo && VIEW_INDEX == VIEW_RIGHT) {
        ALBEDO = texture(view_right, SCREEN_UV).rgb * view_gain;
    } else {
        ALBEDO = texture(view, SCREEN_UV).rgb * view_gain;
    }
}

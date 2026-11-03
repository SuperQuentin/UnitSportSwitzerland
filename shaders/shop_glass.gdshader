// The panes of a shop's or a venue's glazed front (#1292, VenueFront): the fake room behind them, as the
// buildings' windows have (room_behind), one room as wide as the front. UV is where on the front (0..1 across,
// 0..1 up the storey), UV2 the front's width and its storey's height in metres, COLOR the venue's tint, NORMAL
// out to the street. Lit warm at night. See-through glass is #1321.
shader_type spatial;
render_mode cull_back;

#include "res://shaders/common/world.gdshaderinc"

uniform vec3 window_color : source_color = vec3(0.16, 0.19, 0.24);
uniform vec3 window_lit : source_color = vec3(0.95, 0.82, 0.52);
uniform vec2 room_depth = vec2(4.0, 7.0);

float hash(vec2 p) {
    return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453);
}

#define ROOM_SHOP
#include "res://shaders/common/room_behind.gdshaderinc"

varying vec3 world_pos;
varying vec2 tangent;
varying vec2 front_origin;

void vertex() {
    world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    vec3 n = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
    // along the front, the way UV.x grows: up x out
    tangent = normalize(vec2(n.z, -n.x));
    front_origin = world_pos.xz - tangent * UV.x * UV2.x;
}

void fragment() {
    bool lit = world_night > 0.35;
    vec2 cell = floor(front_origin * 2.0);
    vec3 c = room_behind(world_pos, CAMERA_POSITION_WORLD, tangent, UV2.x, UV2.y, UV.x, UV.y, cell, lit, 0.0, TIME);
    c = mix(c, c * COLOR.rgb, 0.6);
    ALBEDO = c * 0.6;
    EMISSION = lit ? c * 0.9 : c * 0.25;
    ROUGHNESS = 0.08;
    SPECULAR = 0.6;
}

shader_type spatial;
render_mode unshaded, cull_disabled, blend_mix, depth_draw_opaque;

#include "res://shaders/common/world.gdshaderinc"
#include "res://shaders/common/retro.gdshaderinc"
#include "res://shaders/world_occasion.gdshaderinc"
#include "res://shaders/door_light.gdshaderinc"

// The GPX route rendered as a raised path. Flat-shaded from screen derivatives like the
// terrain, so the top surface and the side walls separate and the strip reads as a solid
// 3D object rather than a painted line.

uniform float fog_start = 3500.0;
uniform float fog_end = 8000.0;
uniform vec3 fog_color : source_color = vec3(0.72, 0.78, 0.86);
uniform vec3 top_color : source_color = vec3(0.98, 0.74, 0.12);
uniform vec3 side_color : source_color = vec3(0.62, 0.42, 0.06);

// The course line is an orientation aid, and orientation is the last thing you want burnt into
// a finished video. depth_draw_opaque above goes with the blend mode: this is a raised strip
// with real thickness, not a decal, so it still has to occlude itself correctly.
uniform float alpha : hint_range(0.0, 1.0) = 1.0;

varying vec3 world_pos;

void vertex() {
    world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    POSITION = retro_snap(PROJECTION_MATRIX * MODELVIEW_MATRIX * vec4(VERTEX, 1.0));
}

void fragment() {
    vec3 n = normalize(cross(dFdy(world_pos), dFdx(world_pos)));
    if (n.y < 0.0) {
        n = -n;
    }

    // upward-facing triangles are the tread, steep ones are the kerb
    float flat_face = smoothstep(0.45, 0.85, n.y);
    vec3 col = mix(side_color, top_color, flat_face);
    col = occasion_snow(col, n, 0.9);

    float ndl = clamp(dot(n, normalize(world_sun_dir)), 0.0, 1.0);
    col *= retro_steps(ndl, 4.0) * 0.35 + 0.7;

    col *= world_tint + door_light(world_pos, n);
    // valley mist of a running occasion; a no-op on an ordinary day
    col = occasion_mist(col, (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz, length(VERTEX));
    col = retro_posterize(col, FRAGCOORD.xy);

    float fog = smoothstep(fog_start, fog_end, length(VERTEX));
    ALBEDO = mix(col, world_sky, fog);
    ALPHA = alpha;
}

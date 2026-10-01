shader_type spatial;
render_mode unshaded, cull_disabled;

// Building interiors: baked vertex colours (room palette + a darker skirting band), the same
// PS1 treatment as the outside world — vertex snap, a quantised flat light from the
// screen-derivative normal, Bayer colour dither — and no fog: a room is never deep enough for it.
// Vertex alpha < 0.5 marks glass, drawn as the flat colour of the sky outside (World/DayNight's
// globals) so a window reads as a window, and as the hour — noon, sunset, blue hour, night — even
// though the interior floats in the dark under the terrain with nothing to see through it.

#include "res://shaders/common/world.gdshaderinc"
#include "res://shaders/common/retro.gdshaderinc"
#include "res://shaders/portal_clip.gdshaderinc"

uniform vec3 light_direction = vec3(0.35, 0.85, 0.4);
uniform float light_levels = 5.0;
// the sky seen through glass is brighter than the haze it fades into on the horizon: at noon this
// lands on the old fixed glass colour
uniform float glass_gain = 1.3;
// a night window is not a black hole: the dark blue of a moonlit sky
uniform vec3 glass_night = vec3(0.012, 0.016, 0.045);

varying vec3 world_pos;

void vertex() {
    world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    POSITION = retro_snap(PROJECTION_MATRIX * MODELVIEW_MATRIX * vec4(VERTEX, 1.0));
}

void fragment() {
    // a portal camera looking into this room from the shared space under the terrain
    if (portal_clipped(world_pos, CAMERA_POSITION_WORLD)) {
        discard;
    }
    vec3 col = COLOR.rgb;
    if (COLOR.a >= 0.5) {
        // View-space derivatives, not world-space: interiors sit tens of km from the world
        // origin, where a float cannot tell neighbouring pixels apart, the cross product
        // collapses to zero and normalize() returns NaN - black speckle.
        vec3 c = cross(dFdy(VERTEX), dFdx(VERTEX));
        vec3 n = length(c) > 1e-12 ? normalize(c) : vec3(0.0, 0.0, 1.0);
        vec3 l = normalize((VIEW_MATRIX * vec4(light_direction, 0.0)).xyz);
        // soft, mostly-ambient room light: faces toward the light a little brighter
        float ndl = abs(dot(n, l));
        float light = retro_steps(ndl, light_levels) * 0.35 + 0.65;
        col *= light;
    } else {
        col = min(world_sky * glass_gain + glass_night * world_night, vec3(1.0));
    }

    col = retro_posterize(col, FRAGCOORD.xy);
    ALBEDO = col;
}

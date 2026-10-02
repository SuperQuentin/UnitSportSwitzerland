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

// A church turned night club while the chess type beat plays (#370, src/Interiors/ChurchDisco):
// per instance, so only that church's meshes; the room goes dark and the mirror ball's beams and
// sparkles sweep it on the beat. disco 0 draws exactly what was there before.
instance uniform float disco = 0.0;
instance uniform vec3 disco_center = vec3(0.0);
// beats since the CD began, written each frame by ChurchDisco while one plays
global uniform float world_disco_beat;

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

    if (disco > 0.5) {
        float beat = world_disco_beat;
        float pulse = exp(-fract(beat) * 4.0);
        vec3 d = world_pos - disco_center;
        float az = atan(d.z, d.x);
        float el = atan(d.y, length(d.xz));
        // coloured beams from the ball, turning half a turn a bar, hue stepping on every beat
        float beams = smoothstep(0.86, 0.96, cos(6.0 * az - beat * 0.785) * cos(4.0 * el + beat * 0.4));
        float hue = fract(az / 6.2832 * 2.0 + floor(beat) * 0.17);
        vec3 beam = 0.5 + 0.5 * cos(6.2832 * (hue + vec3(0.0, 0.33, 0.67)));
        // the mirror ball's sparkles, drifting the other way
        float sparkle = step(0.985, cos(46.0 * az + beat * 1.3) * cos(46.0 * el - beat * 0.9));
        col = col * (0.22 + 0.12 * pulse) + beam * (beams * (0.45 + 0.35 * pulse)) + vec3(sparkle * 0.5);
    }

    col = retro_posterize(col, FRAGCOORD.xy);
    ALBEDO = col;
}

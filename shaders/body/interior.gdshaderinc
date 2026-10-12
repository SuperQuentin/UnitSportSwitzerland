// Interior body (#388), shared by every style's interior wrapper (ps1_interior, cartoon_interior,
// real_interior). Each wrapper is shader_type, render_mode, its defines, then this include.
//
// Building interiors float in the dark 3 km under the terrain, out of Godot's sun, so every style
// lights them itself, unshaded, the way outdoor surfaces are lit: by the hour. Baked vertex colours
// (room palette, a darker skirting band) times
//   - the room's ambient: the outdoor light (world_tint) a little dimmed, as in a real room,
//   - daylight from each window (Interiors/RoomLights): brighter near it and on what faces it,
//   - the sun's patch through each window, on the floor and the wall opposite,
//   - a ceiling lamp per room: on at night, and all day in a room with no window.
// The same formula in every style, so a room by noon, at sunset or at midnight reads like the
// street outside it. Vertex alpha < 0.5 marks glass, drawn as the flat colour of the sky outside.
//   INTERIOR_CARTOON  the light in soft bands, colours toward pastel (style_grade)
//   INTERIOR_REAL     plaster on walls and ceilings, boards on wooden floors (style_tinted)
// Without either: the PS1 finish (vertex snap, light steps, Bayer dither), behind `retro`.

#ifdef INTERIOR_REAL
#define STYLE_REAL
#endif

#include "res://shaders/common/world.gdshaderinc"
#include "res://shaders/common/retro.gdshaderinc"
#include "res://shaders/portal_clip.gdshaderinc"
#ifdef INTERIOR_REAL
#include "res://shaders/common/style.gdshaderinc"
#endif

uniform float light_levels = 5.0;
// the sky seen through glass is brighter than the haze it fades into on the horizon: at noon this
// lands on the old fixed glass colour
uniform float glass_gain = 1.3;
// a night window is not a black hole: the dark blue of a moonlit sky
uniform vec3 glass_night = vec3(0.012, 0.016, 0.045);

// --- the light (Interiors/RoomLights, per interior) ----------------------------------------
uniform sampler2D room_lights : filter_nearest;
// world -> interior-local (the node's inverse transform; InteriorNode keeps it)
uniform mat4 interior_frame = mat4(1.0);
uniform float floor_base = 0.0;
uniform float storey_height = 3.0;
uniform int floor_count = 0;
// how much of the outdoor light a room gets everywhere, how much a window adds, the sun's patch
uniform float ambient_day = 0.42;
uniform float window_gain = 1.0;
uniform float sun_gain = 0.9;
uniform float lamp_gain = 0.75;
uniform vec3 lamp_color = vec3(1.0, 0.70, 0.40);
// the style's exposure next to its outdoor surfaces, which Godot's sun lights (StyleKit sets it)
uniform float light_gain = 1.0;

// A church turned night club while the chess type beat plays (#370, src/Interiors/ChurchDisco):
// per instance, so only that church's meshes; the room goes dark and the mirror ball's beams and
// sparkles sweep it on the beat. disco 0 draws exactly what was there before.
instance uniform float disco = 0.0;
instance uniform vec3 disco_center = vec3(0.0);
// beats since the CD began, written each frame by ChurchDisco while one plays
global uniform float world_disco_beat;

varying vec3 world_pos;
// the mesh's own space: the room's is the building's frame (its mesh sits at the interior node), a
// door leaf's, a lift door's or a pallet's moves with it, so their plaster and boards do too (#724)
varying vec3 local_pos;

#ifdef INTERIOR_REAL
uniform sampler2D tex_plaster : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
#endif
#ifdef INTERIOR_CARTOON
vec3 style_grade(vec3 col) {
    float l = dot(col, vec3(0.2126, 0.7152, 0.0722));
    col = mix(vec3(l), col, 0.92);
    return pow(max(col, vec3(0.0)), vec3(0.92)) * 0.95;
}
#endif

float room_hash(vec2 p) {
    return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453);
}

// The light falling on local point p with local normal n, linear, before the style's gain.
vec3 room_light(vec3 p, vec3 n) {
    float day = 1.0 - world_night;
    vec3 total = world_tint * ambient_day;
    if (floor_count <= 0) {
        // no table (a probe's or demo's room): a plain lit room
        return total + lamp_color * lamp_gain * 0.6 * world_night;
    }
    int f = clamp(int(floor((p.y - floor_base + 0.1) / storey_height)), 0, floor_count - 1);
    vec4 range = texelFetch(room_lights, ivec2(f, 0), 0);
    int first = int(range.x + 0.5);
    int count = int(range.y + 0.5);
    // the sun, into this room's frame; world_sun_dir follows the moon at night, which day hides
    vec3 sun = normalize(mat3(interior_frame) * world_sun_dir);
    vec3 sky_light = mix(world_tint, world_sky * glass_gain, 0.35);
    for (int i = 0; i < 256; i++) {
        if (i >= count) {
            break;
        }
        int x = (first + i) * 4;
        vec4 rect = texelFetch(room_lights, ivec2(x + 2, 1), 0);
        if (p.x < rect.x || p.x > rect.z || p.z < rect.y || p.z > rect.w) {
            continue;
        }
        vec4 ys = texelFetch(room_lights, ivec2(x + 3, 1), 0);
        if (p.y < ys.x || p.y > ys.y) {
            continue;
        }
        vec4 c = texelFetch(room_lights, ivec2(x, 1), 0);
        vec4 a = texelFetch(room_lights, ivec2(x + 1, 1), 0);
        vec3 to = c.xyz - p;
        float d = max(length(to), 0.05);
        vec3 dir = to / d;
        if (c.w < 0.5) {
            // a window: daylight, more on what faces it, falling off over a few metres
            vec3 inward = vec3(a.x, 0.0, a.y);
            float area = 4.0 * a.z * a.w;
            float facing = clamp(dot(n, dir) * 0.6 + 0.4, 0.0, 1.0);
            total += sky_light * window_gain * min(area / (1.0 + 0.35 * d * d), 0.65) * facing;
            // the sun's patch: from p toward the sun, through the opening?
            float s_out = -dot(sun, inward);
            if (day > 0.0 && s_out > 0.05) {
                float t = dot(-to, inward) / s_out;
                if (t > 0.0) {
                    vec3 rel = p + sun * t - c.xyz;
                    float along = dot(rel.xz, vec2(-inward.z, inward.x));
                    if (abs(along) < a.z && abs(rel.y) < a.w) {
                        total += world_tint * sun_gain * day * clamp(dot(n, sun), 0.0, 1.0);
                    }
                }
            }
        } else {
            // a ceiling lamp: on at night, or always in a windowless room
            float on = max(world_night, a.y);
            float fall = 1.0 - clamp(d / a.x, 0.0, 1.0);
            float facing = clamp(dot(n, dir) * 0.5 + 0.5, 0.0, 1.0);
            total += lamp_color * lamp_gain * on * (0.12 + 0.88 * fall * fall) * (0.4 + 0.6 * facing);
        }
    }
    return total;
}

void vertex() {
    world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    local_pos = VERTEX;
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
        vec3 nv = length(c) > 1e-12 ? normalize(c) : vec3(0.0, 0.0, 1.0);
        // toward the viewer: rooms are seen from inside, whichever way a face was wound
        if (dot(nv, VERTEX) > 0.0) {
            nv = -nv;
        }
        vec3 n = normalize(mat3(interior_frame) * (mat3(INV_VIEW_MATRIX) * nv));
        vec3 p = (interior_frame * vec4(world_pos, 1.0)).xyz;
#ifdef INTERIOR_REAL
        // plaster on walls and ceilings in the wall's own plane; boards on wooden floors: laid out
        // in the mesh's own space, so a swinging door or a carried pallet takes its texture along
        vec3 ln = normalize(transpose(mat3(MODEL_MATRIX)) * (mat3(INV_VIEW_MATRIX) * nv));
        vec3 lp = local_pos;
        if (ln.y > 0.7 && col.r > col.b * 1.25) {
            vec2 q = lp.xz;
            float plank = floor(q.x / 0.16);
            float seam = smoothstep(0.0, 0.012, fract(q.x / 0.16) * 0.16) * smoothstep(0.0, 0.012, (1.0 - fract(q.x / 0.16)) * 0.16);
            float joint = floor(q.y / 1.6 + room_hash(vec2(plank, 1.0)));
            col *= (0.88 + 0.24 * room_hash(vec2(plank, joint))) * mix(0.55, 1.0, seam)
                * (0.94 + 0.06 * sin(q.y * 23.0 + plank * 3.1));
        } else {
            vec2 uv = abs(ln.y) > 0.7 ? lp.xz : vec2(abs(ln.x) > abs(ln.z) ? lp.z : lp.x, lp.y);
            col = style_tinted(tex_plaster, uv / 3.0, col, 0.1);
        }
#endif
        // a soft directional cue so corners and box sides read, as outdoors but milder
        float ndl = clamp(dot(n, normalize(vec3(0.35, 0.85, 0.4))) * 0.5 + 0.5, 0.0, 1.0);
        vec3 light = room_light(p, n) * (retro_steps(ndl, light_levels) * 0.3 + 0.7);
#ifdef INTERIOR_CARTOON
        // the light in soft bands, as the cel-shaded street
        float l = max(dot(light, vec3(0.3333)), 1e-4);
        float banded = mix(floor(l * 4.0) / 4.0, ceil(l * 4.0) / 4.0, smoothstep(0.35, 0.65, fract(l * 4.0)));
        light *= max(banded, 0.05) / l;
#endif
        col *= light * light_gain;
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

#ifdef INTERIOR_CARTOON
    col = style_grade(col);
#endif
    col = retro_posterize(col, FRAGCOORD.xy);
    ALBEDO = col;
}

// Building body, shared by every style's building wrapper (ps1_building). Each wrapper is
// shader_type, render_mode, its defines, then this include.

#include "res://shaders/common/world.gdshaderinc"
#include "res://shaders/common/retro.gdshaderinc"
#include "res://shaders/common/style.gdshaderinc"
#include "res://shaders/common/sightline.gdshaderinc"
#include "res://shaders/world_occasion.gdshaderinc"
#include "res://shaders/door_light.gdshaderinc"
#include "res://shaders/portal_clip.gdshaderinc"
#include "res://shaders/world_origin.gdshaderinc"

// Buildings take their base colour from baked vertex colours (kind + roof/wall + age)
// and get the same PS1 treatment as the terrain: vertex snap, quantised sun from a
// screen-derivative flat normal, Bayer dither, distance fog.

uniform float light_levels = 5.0;
uniform float fog_start = 3500.0;
uniform float fog_end = 8000.0;
uniform vec3 fog_color : source_color = vec3(0.72, 0.78, 0.86);

uniform vec3 window_color : source_color = vec3(0.16, 0.19, 0.24);
uniform vec3 window_lit : source_color = vec3(0.95, 0.82, 0.52);
uniform float window_spacing = 3.2;
// how deep the fake room behind a window runs, metres (min, max; picked per window)
uniform vec2 room_depth = vec2(3.0, 5.5);

// --- occupied buildings (Interiors/InteriorManager, via ChunkManager.SetOccupancy) --------
// Buildings with players inside: their plan box (world xz centre, half width along the axis,
// half depth across it) and (axis cos, axis sin, how busy 0..1). More of their windows are lit
// and some show a figure moving about the fake room behind the glass.
uniform vec4 occupied_box[8];
uniform vec4 occupied_axis[8];
uniform int occupied_count = 0;

// --- open doors (Interiors/DoorPortals, via ChunkManager.SetOpenDoors) --------------------
// Doors whose portal is showing: the doorway frame's origin (on the sill, in the facade's plane)
// and half its width, then (outward x, outward z, its height). Their baked closed leaf and handle
// (BuildingMeshBuilder.AppendDoor: 6..11 cm proud, inside the opening) are dropped, so the
// portal can stand right on the facade. The frame, lintel and doorstep stay: beside, above and
// below the opening. The leaf's foot, inside the doorstep, goes too, but only in the leaf's own
// plane: the step has no back face, and looking out from inside, the foot showed through it.
uniform vec4 open_door_box[16];
uniform vec4 open_door_axis[16];
uniform int open_door_count = 0;

bool open_door_leaf(vec3 p) {
    for (int i = 0; i < 16; i++) {
        if (i >= open_door_count) {
            break;
        }
        vec3 d = p - open_door_box[i].xyz;
        vec2 o = open_door_axis[i].xy;
        float out_ = d.x * o.x + d.z * o.y;
        float along = d.z * o.x - d.x * o.y;
        // a hair inside the opening: the jambs' and the lintel's own faces bound it exactly
        if (abs(along) >= open_door_box[i].w - 0.002 || d.y >= open_door_axis[i].z - 0.002) {
            continue;
        }
        // leaf and handles above the step; below its top (12 cm), the leaf's plane alone
        if ((out_ > 0.045 && out_ < 0.125 && d.y > 0.125)
            || (out_ > 0.05 && out_ < 0.07 && d.y > -0.01 && d.y < 0.119)) {
            return true;
        }
    }
    return false;
}

// garages (BuildingMeshBuilder flags them in UV2.y): the sign's light face over the door (lit
// from behind at night), and a painted stripe under the eave that reads from across the valley
uniform vec3 garage_sign : source_color = vec3(0.97, 0.95, 0.88);
uniform vec3 garage_stripe : source_color = vec3(0.82, 0.24, 0.12);

varying vec3 world_pos;

#ifdef STYLE_REAL
uniform sampler2D tex_plaster : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D tex_roof : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
#endif

varying vec2 facade;   // x = metres along the facade, y = storey coordinate (<0 = none)
varying float storey_count; // whole storeys in the wall; grid stops below this
varying vec3 wall_frame; // xy = the wall's horizontal tangent (x, z), z = storey height in metres

float hash(vec2 p) {
    return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453);
}

// How busy the building at p is: 0 nobody inside, up to 1.
float occupancy(vec3 p) {
    float busy = 0.0;
    for (int i = 0; i < 8; i++) {
        if (i >= occupied_count) {
            break;
        }
        vec2 d = p.xz - occupied_box[i].xy;
        vec2 ax = occupied_axis[i].xy;
        if (abs(dot(d, ax)) < occupied_box[i].z + 0.6 && abs(dot(d, vec2(-ax.y, ax.x))) < occupied_box[i].w + 0.6) {
            busy = max(busy, occupied_axis[i].z);
        }
    }
    return busy;
}

// --- interior mapping ------------------------------------------------------------------
// A room behind every window, without any geometry: the view ray enters at the glass and is
// followed to the first of the room's planes, in the wall's own frame - u along the facade
// (one room per window column), v up the storey, d into the building. The room's colours,
// depth and furniture come from the window's hash, so a house reads the same every visit.
// lu01 / lv01 are where the ray enters, as a fraction of the cell and the storey.
vec3 room_behind(vec3 p, vec3 eye, vec2 tangent, float h, float lu01, float lv01, vec2 cell,
                 bool lit, float busy, float time) {
    float w = window_spacing;
    vec3 t = normalize(vec3(tangent.x, 0.0, tangent.y));
    vec3 v = normalize(p - eye);
    // into the building is away from the eye, whichever way the TIN triangle was wound
    vec3 n = vec3(-t.z, 0.0, t.x);
    float dd = dot(v, n);
    if (dd < 0.0) {
        n = -n;
        dd = -dd;
    }
    vec3 dim = lit ? window_lit : window_color;
    if (dd < 0.03) {
        return dim; // grazing: all glass
    }
    float du = dot(v, t);
    float dv = v.y;
    float lu = lu01 * w;
    float lv = lv01 * h;
    float ceil_h = h - 0.25; // the slab of the floor above
    float depth = mix(room_depth.x, room_depth.y, hash(cell + 7.13));

    float tu = du > 1e-4 ? (w - lu) / du : (du < -1e-4 ? -lu / du : 1e6);
    float tv = dv > 1e-4 ? (ceil_h - lv) / dv : (dv < -1e-4 ? -lv / dv : 1e6);
    float td = depth / dd;
    float th = min(min(tu, tv), td);
    vec3 hit = vec3(lu + du * th, lv + dv * th, dd * th);

    float k = hash(cell + 3.71);
    vec3 wall = k < 0.2 ? vec3(0.86, 0.80, 0.68) : k < 0.4 ? vec3(0.70, 0.78, 0.84)
              : k < 0.6 ? vec3(0.74, 0.80, 0.66) : k < 0.8 ? vec3(0.84, 0.72, 0.68) : vec3(0.62, 0.46, 0.32);
    vec3 c;
    if (th == td) {
        c = wall * 0.92;
        // a cupboard, a sofa or a shelf against the back wall, and a picture beside it
        float fx = hash(cell + 5.9) * (w - 1.2);
        float fh = mix(0.8, 1.9, hash(cell + 9.1));
        if (hit.x > fx && hit.x < fx + 1.1 && hit.y < fh) {
            c = mix(vec3(0.35, 0.24, 0.16), vec3(0.45, 0.30, 0.35), hash(cell + 2.2));
        } else if (abs(hit.x - (fx > w * 0.5 - 0.6 ? fx - 0.7 : fx + 1.8)) < 0.3 && abs(hit.y - 1.6) < 0.22) {
            c = vec3(0.30, 0.38, 0.30);
        }
    } else if (th == tv) {
        c = dv < 0.0 ? (hash(cell + 1.3) < 0.6 ? vec3(0.46, 0.32, 0.20) : vec3(0.55, 0.55, 0.52))
                     : vec3(0.92, 0.91, 0.88);
    } else {
        c = wall * 0.78;
    }
    // lit from the window side: the far end of the room is darker
    c *= mix(1.0, 0.55, clamp(hit.z / depth, 0.0, 1.0));
    c *= lit ? window_lit * 1.15 : mix(vec3(0.42, 0.44, 0.46), vec3(0.06, 0.07, 0.10), world_night);

    // someone at home: a figure crossing the room a metre and a half back from the glass
    if (busy > 0.0 && hash(cell + 11.7) < 0.55 * busy) {
        float tf = 1.4 / dd;
        if (tf < th) {
            vec2 q = vec2(lu + du * tf, lv + dv * tf);
            float x0 = w * 0.5 + sin(time * (0.25 + 0.2 * hash(cell)) + hash(cell + 4.4) * 6.28) * w * 0.32;
            if ((abs(q.x - x0) < 0.21 && q.y < 1.42) || length(q - vec2(x0, 1.60)) < 0.12) {
                c = lit ? vec3(0.05, 0.04, 0.04) : c * 0.35;
            }
        }
    }

    // Only a hint of a room gets through: seen plainly, it is obviously a painted box. Softened
    // toward its own average (old glass, dust, distance), behind a net curtain in half the
    // windows and drawn side curtains in the rest, and under a strong reflection of the sky.
    vec3 average = wall * (lit ? window_lit : mix(vec3(0.30), vec3(0.05), world_night));
    c = mix(c, average, 0.45);
    float drape = hash(cell + 8.8);
    if (drape < 0.5) {
        // a sheer net curtain across the whole pane: a figure behind a lit one still shows
        vec3 net = lit ? window_lit * 0.95 : vec3(0.78, 0.78, 0.74) * mix(1.0, 0.12, world_night);
        c = mix(c, net, 0.65);
    } else if (lu01 < 0.36 || lu01 > 0.64) {
        vec3 cloth = drape < 0.67 ? vec3(0.45, 0.14, 0.12) : drape < 0.84 ? vec3(0.22, 0.34, 0.24) : vec3(0.70, 0.62, 0.48);
        c = cloth * (lit ? window_lit * 0.8 : mix(vec3(0.6), vec3(0.08), world_night));
    }
    float fres = 0.35 + 0.4 * pow(1.0 - dd, 2.0);
    vec3 glass = mix(window_color, world_sky, 0.55);
    c = mix(c, glass, lit ? fres * 0.35 : fres);
    // a mullion and a transom
    if (abs(lu01 - 0.5) < 0.012 || abs(lv01 - 0.62) < 0.012) {
        c = vec3(0.86, 0.85, 0.82) * (lit ? 0.7 : 1.0);
    }
    return c;
}

// A church nave's one tall window row: leaded coloured glass, no room behind it.
vec3 stained_glass(vec2 q, vec2 cell, bool lit) {
    vec2 g = floor(q / 0.32);
    vec2 f = fract(q / 0.32);
    float k = hash(g + cell * 3.1);
    vec3 c = k < 0.25 ? vec3(0.55, 0.10, 0.10) : k < 0.5 ? vec3(0.12, 0.22, 0.55)
           : k < 0.75 ? vec3(0.70, 0.55, 0.12) : vec3(0.14, 0.42, 0.20);
    if (f.x < 0.1 || f.y < 0.1) {
        c = vec3(0.05); // lead
    }
    return c * (lit ? 1.4 : mix(0.7, 0.25, world_night));
}

varying float garage_flag;  // UV2.y: 0 plain, 1 neon sign, 2 garage wall (storey_count = eave m)

void vertex() {
    world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    facade = UV;
    storey_count = UV2.x;
    wall_frame = CUSTOM0.xyz;
    garage_flag = UV2.y;
    POSITION = retro_snap(PROJECTION_MATRIX * MODELVIEW_MATRIX * vec4(VERTEX, 1.0));
}

void fragment() {
    if (portal_clipped(world_pos, CAMERA_POSITION_WORLD) || open_door_leaf(world_pos)) {
        discard;
    }
    vec3 n = normalize(cross(dFdy(world_pos), dFdx(world_pos)));
    // faces are not consistently wound in the source TINs, so orient toward the viewer
    if (!FRONT_FACING) {
        n = -n;
    }

    vec3 col = COLOR.rgb;
#ifdef STYLE_REAL
    // walls: plaster in facade space (metres along, storeys times their height); roofs: tiles
    // in plan. Windows, signs and paint are drawn over it below.
    if (n.y > 0.25 || facade.y < 0.0) {
        col = style_tinted(tex_roof, vec2(pattern_xz(world_pos).x + world_pos.y * 0.7, pattern_xz(world_pos).y) / 2.5, col, 0.25);
    } else {
        col = style_tinted(tex_plaster, vec2(facade.x, facade.y * max(wall_frame.z, 2.8)) / 3.0, col, 0.15);
    }
#endif
    // how much of this pixel is glass (windows): glossy in the lit styles
    float glass = 0.0;
    // a running occasion's snow on the roofs (walls are vertical and stay clear)
    col = occasion_snow(col, n, 1.0);

    // --- windows -------------------------------------------------------------------
    // Storey height comes from the GWR floor count (baked into UV.y; 0 opts out, and
    // roof faces are always 0). The horizontal axis is derived from the wall's own
    // normal so the grid runs along each facade instead of along world axes.
    // Drop the pattern once a storey is thinner than a couple of pixels, otherwise it
    // aliases into confetti at the low internal render resolution.
    float detail = fwidth(facade.y);
    // stop a little below the wall plate so the eave never cuts a row in half
    float top = storey_count - 0.22;
    vec3 glow = vec3(0.0);
    bool garage_wall = garage_flag > 1.5;
    if (!garage_wall && facade.y > 0.0 && facade.y < top && detail < 0.6) {
        float u = facade.x;
        float wall_v = fract(facade.y);
        float col_u = fract(u / window_spacing);
        bool in_row = wall_v > 0.32 && wall_v < 0.76;
        bool in_col = col_u > 0.30 && col_u < 0.70;

        if (facade.y > 0.35 && in_row && in_col) {
            // stable per-window hash decides which ones are lit
            vec2 cell = vec2(floor(u / window_spacing), floor(facade.y));
            float r = hash(cell);
            // someone inside lights more of them
            float busy = occupancy(world_pos);
            // a few lit windows by day, most of a village at night
            bool lit = r > mix(0.86, 0.35 - 0.2 * world_lights, world_night) - busy * 0.45;
            vec3 win = lit ? window_lit : window_color;
            // fade the pattern out as it approaches the resolution limit
            float legible = clamp(1.0 - detail / 0.6, 0.0, 1.0);
            // close enough to see into: a room behind it (a church's tall storey gets coloured glass)
            float storey_h = wall_frame.z;
            if (legible > 0.05 && storey_h > 0.0) {
                if (storey_h > 5.5) {
                    win = stained_glass(vec2(col_u * window_spacing, wall_v * storey_h), cell, lit);
                } else {
                    win = room_behind(world_pos, CAMERA_POSITION_WORLD, wall_frame.xy, storey_h,
                        col_u, wall_v, cell, lit, busy, TIME);
                }
            }
            col = mix(col, win, legible);
            glass = legible;
            if (lit) glow = win * legible;
        }
    }

    // --- festive lights (a running occasion's world_lights) ---------------------------
    // A string of bulbs along the eaves, just under the wall plate, on about 60% of the houses
    // that have a window grid. Drawn here rather than as geometry: every house in view gets them
    // for the cost of a few instructions, and they follow the facade frame the windows use.
    if (!garage_wall && world_lights > 0.0 && storey_count > 0.5 && detail < 0.6) {
        float house = fract(sin(dot(floor(pattern_xz(world_pos) / 24.0), vec2(41.3, 17.9))) * 9731.7);
        float eave = storey_count - 0.05;
        if (house < 0.6 && facade.y > eave - 0.08 && facade.y < eave) {
            float f = fract(facade.x / 0.5);
            if (f > 0.35 && f < 0.65) {
                int k = int(mod(floor(facade.x / 0.5), 4.0));
                vec3 bulb = k == 0 ? vec3(1.0, 0.12, 0.08) : k == 1 ? vec3(0.10, 0.90, 0.20)
                          : k == 2 ? vec3(0.20, 0.40, 1.00) : vec3(1.00, 0.80, 0.30);
                float legible = clamp(1.0 - detail / 0.6, 0.0, 1.0);
                col = mix(col, bulb * 0.35, legible);
                glow = max(glow, bulb * legible * world_lights);
            }
        }
    }

    // --- garage: the sign's light face, and a painted stripe under the eave -------------
    // The face is a light box: plain paint by day, lit at night like a window. The stripe is
    // paint, shaded like the wall, 0.5 m tall so it still reads as a line a kilometre off.
    if (garage_flag > 0.5 && garage_flag < 1.5) {
        col = garage_sign;
        glow = max(glow, garage_sign);
    } else if (garage_wall && facade.y > storey_count - 0.75 && facade.y < storey_count - 0.25) {
        col = garage_stripe;
    }

#ifndef STYLE_LIT
    float ndl = clamp(dot(n, normalize(world_sun_dir)), 0.0, 1.0);
    float light = retro_steps(ndl, light_levels) * 0.5 + 0.5;
    col *= light;
#endif

    // dissolve anything between the camera and its subject, using the same 4x4 Bayer pattern
    // the colour quantiser below uses, so the fade reads as part of the renderer
    float keep = sightline_keep(world_pos);
    if (keep < 1.0 && keep < bayer4(FRAGCOORD.xy)) {
        discard;
    }

#ifdef STYLE_LIT
    // valley mist of a running occasion; a no-op on an ordinary day
    col = style_grade(occasion_mist(col, (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz, length(VERTEX)));
    ALBEDO = col;
    NORMAL = style_view_normal(VIEW_MATRIX, n);
#ifdef STYLE_REAL
    ROUGHNESS = mix(0.9, 0.08, glass);
    SPECULAR = mix(0.5, 0.9, glass);
#else
    ROUGHNESS = 1.0;
#endif
    // a lit window is its own light: at night it shines through the darkness around it
    EMISSION = col * door_light(world_pos, n) + glow * world_night;
#else
    col *= world_tint + door_light(world_pos, n);
    // a lit window is its own light: at night it shines through the darkness around it
    col = max(col, glow * world_night);
    // valley mist of a running occasion; a no-op on an ordinary day
    col = occasion_mist(col, (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz, length(VERTEX));
    col = retro_posterize(col, FRAGCOORD.xy);

    float fog = smoothstep(fog_start, fog_end, length(VERTEX));
    ALBEDO = mix(col, world_sky, fog);
#endif
}

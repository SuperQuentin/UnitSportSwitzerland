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

// --- facade relief ---------------------------------------------------------------------
// Depth without geometry, in the facade frame the windows use (u along, v up the storey, d into
// the wall): the glass sits `window_recess` behind the facade and the view ray is traced into
// the opening, so the jambs, the head and the sill show and shift with the camera; a stone sill
// stands under each window, a string course at each floor slab, a plinth at the foot, and part
// of the houses have louvred shutters. All of it fades with the window grid at a distance.
uniform float window_recess = 0.22;
uniform float plinth_height = 0.45;

// Follows the view ray d (along, up, into) from q on the facade into an opening [lo, hi] set
// `depth` into the wall. Returns the distance travelled; side 0 = the glass, 1 = a jamb,
// 2 = the head, 3 = the sill.
float recess_trace(vec2 q, vec3 d, vec2 lo, vec2 hi, float depth, out int side) {
    float tg = depth / max(d.z, 1e-4);
    float tu = d.x > 1e-4 ? (hi.x - q.x) / d.x : (d.x < -1e-4 ? (lo.x - q.x) / d.x : 1e6);
    float tv = d.y > 1e-4 ? (hi.y - q.y) / d.y : (d.y < -1e-4 ? (lo.y - q.y) / d.y : 1e6);
    float t = min(tg, min(tu, tv));
    side = t == tg ? 0 : (t == tu ? 1 : (d.y > 0.0 ? 2 : 3));
    return t;
}

// 0 where a point z deep in the recess is in the reveals' shadow, else 1. s = the sun in the
// facade frame (along, up, out of the wall); a sun behind the wall or below the horizon leaves
// the whole wall in shade already, so nothing more is taken off.
float recess_sun(vec2 q, float z, vec3 s, vec2 lo, vec2 hi) {
    if (s.z <= 0.02 || s.y <= 0.0) {
        return 1.0;
    }
    vec2 e = q + s.xy * (z / s.z);
    return (e.x > lo.x && e.x < hi.x && e.y > lo.y && e.y < hi.y) ? 1.0 : 0.0;
}

// Louvred shutter at q (metres inside the leaf of width w, height h): a frame and slats whose
// tops catch the sky and whose undersides fall into shade. `fine` fades the slats into the
// leaf's average shade as they near a pixel.
vec3 shutter(vec2 q, float w, float h, vec3 paint, float fine) {
    if (q.x < 0.05 || q.x > w - 0.05 || q.y < 0.05 || q.y > h - 0.05) {
        return paint;
    }
    float f = fract(q.y / 0.075);
    return paint * mix(0.93, mix(0.68, 1.18, f), fine);
}

// How fully a feature of `size` metres shows when a pixel spans px metres: gone under ~1 px.
float fine_detail(float size, float px) {
    return clamp(size / max(px, 1e-4) - 1.0, 0.0, 1.0);
}

// 0 below `edge`, 1 above, blended over `w` (a pixel in the same units) instead of a hard step
float band(float edge, float x, float w) {
    return smoothstep(edge - w * 0.5, edge + w * 0.5, x);
}

// --- roof relief -------------------------------------------------------------------------
// BuildingMeshBuilder gives roofs a frame of their own in CUSTOM0.w (0 = none):
// 1, a pitched roof: CUSTOM0.xyz the unit up-slope vector, UV.x metres along the eave, UV2 =
//    (metres up from the eave, the eave-to-ridge length). Tile courses, a gutter, ridge caps.
// 2, a large flat roof with solar panels: CUSTOM0 = (its extent along the building's axis,
//    across it, the across direction's angle), UV.x / UV2.x metres along / across from its edge.
varying float roof_kind;
varying vec2 roof_uv;  // (along, up the slope) or (along, across)
// flat: one value per face. Interpolated it wobbled by a last bit from pixel to pixel, and the
// hash of it (which tiles, where the moss is) flipped whole tiles as the camera crept (#793)
varying flat float roof_len; // eave to ridge, metres

// Tile courses on a pitched roof: beaver tails (17 cm courses, half-offset, rounded ends) or
// pantiles (33 cm courses with a wave across). Each course's butt edge stands proud, faces down
// the slope and shades the course below; a gutter at the eave, capping along the ridge, a little
// moss. q = (along, up from the eave), px = metres a pixel spans.
vec3 roof_tiles(vec3 col, inout vec3 n, vec3 up, vec2 q, float ridge, float px) {
    float kind = hash(vec2(round(ridge * 20.0) * 0.185, 1.3));   // to 5 cm: a stable seed
    // gone by a 6 cm pixel: its lips and shadow lines are 2-3 cm, and at full strength past that
    // they turned to moire that crawled as the camera moved (#793). Each edge is also smoothed
    // over one pixel (band(), w = a pixel in the pattern's own units).
    float shown = fine_detail(0.06, px);
    if (q.y < 0.14) {
        return mix(col, vec3(0.30, 0.29, 0.27), fine_detail(0.14, px)); // the gutter
    }
    if (q.y > ridge - 0.24) {
        float joint = fract(q.x / 0.42);
        float dark = 1.0 - band(0.08, joint, px / 0.42);
        return mix(col, col * mix(0.9, 0.62, dark), fine_detail(0.07, px)); // ridge caps
    }
    vec3 c;
    float lip = 0.0;   // how much of the pixel is a course's lip: its normal tilts by that much
    vec2 tile;
    if (kind < 0.55) {
        float k = floor(q.y / 0.17);
        float u = q.x / 0.18 + 0.5 * mod(k, 2.0);
        float fx = fract(u) * 2.0 - 1.0;
        float fy = fract(q.y / 0.17);
        float edge = 0.38 * (1.0 - sqrt(max(0.0, 1.0 - fx * fx)));
        tile = vec2(floor(u), k);
        float w = px / 0.17;
        float past = band(edge, fy, w);                  // out of the shadow of the course above
        float on_lip = past * (1.0 - band(edge + 0.12, fy, w));
        c = col * mix(mix(1.04, 0.9, fy), 1.12, on_lip);
        c = mix(col * 0.62, c, past);                    // the course beneath, in this one's shadow
        lip = on_lip;
    } else {
        float k = floor(q.y / 0.33);
        float fy = fract(q.y / 0.33);
        float wave = sin(q.x / 0.21 * 6.2832);
        vec3 along = normalize(vec3(-up.z, 0.0, up.x));
        n = normalize(n + along * wave * 0.35 * shown);
        tile = vec2(floor(q.x / 0.21), k);
        c = col * (1.0 + 0.1 * wave);
        float w = px / 0.33;
        float on_lip = 1.0 - band(0.07, fy, w);
        float under = band(0.9, fy, w);                  // under the next course's edge
        c *= mix(1.0, 1.1, on_lip) * mix(1.0, 0.7, under);
        lip = on_lip;
    }
    c *= 0.93 + 0.14 * hash(tile + kind * 17.0);
    // moss and lichen: patches, thicker toward the damp eave
    float moss = step(0.86, hash(floor(q / 0.9) + kind * 5.0)) * 0.35 + 0.25 * clamp(1.0 - q.y / 1.5, 0.0, 1.0);
    c = mix(c, vec3(0.24, 0.29, 0.14) * (0.8 + 0.4 * hash(tile)), moss * hash(tile + 3.3));
    // weighted, not on/off: a lip's steep normal flipped whole pixels' light as the camera crept (#793)
    n = normalize(mix(n, normalize(n - up * 1.2), shown * lip));
    return mix(col, c, shown);
}

// Solar panels in rows on a large flat roof: 1 x 1.7 m modules tilted toward the south, rows
// 2.7 m apart with each row's shadow behind it, a 1.2 m margin at the roof's edge. Each roof
// has its own age and cell type (black mono or blue poly); the older, the more of its modules
// are yellowed, dusty at the low edge, spotted by birds, cracked, or missing down to the rails.
// q = (along, across) from the roof's edge, ext its extent, ang the across direction's angle.
vec3 solar_field(vec3 col, inout vec3 n, inout float glass, vec2 q, vec2 ext, float ang, float px) {
    float m = 1.2;
    if (q.x < m || q.x > ext.x - m || q.y < m || q.y > ext.y - m) {
        return col;
    }
    vec3 across = vec3(cos(ang), 0.0, sin(ang));
    float sg = across.z >= 0.0 ? 1.0 : -1.0; // +z is south
    float cn = sg > 0.0 ? ext.y - m - q.y : q.y - m; // metres north of the southern margin
    float depth = 1.7;
    float row = floor(cn / 2.7);
    float r = cn - row * 2.7; // from the module's low (south) edge
    float sun_y = clamp(normalize(world_sun_dir).y, 0.05, 1.0);
    float shadow_len = clamp(0.45 * sqrt(1.0 - sun_y * sun_y) / sun_y, 0.15, 0.95);
    float near = fine_detail(0.6, px);
    vec3 avg = mix(col, vec3(0.08, 0.10, 0.16), 0.6);
    if (r > depth) {
        return mix(avg, r < depth + shadow_len ? col * 0.55 : col, near);
    }
    float mi = floor((q.x - m) / 1.02);
    vec2 cell = vec2(mi, row);
    vec2 mq = vec2(q.x - m - mi * 1.02, r);
    if (mq.x > 1.0) {
        return mix(avg, col * 0.4, near); // the gap between two modules
    }
    float roof = hash(vec2(ext.x * 3.1, ext.y * 1.7));
    float age = fract(roof * 7.3);
    float mh = hash(cell + roof * 13.0);
    if (mh < 0.035 * age) {
        // a missing module: the roof and the two rails it sat on
        return mix(avg, abs(mq.y - 0.4) < 0.04 || abs(mq.y - 1.3) < 0.04 ? vec3(0.6) : col * 0.6, near);
    }
    bool mono = roof < 0.5;
    vec3 p = mono ? vec3(0.025, 0.03, 0.045) : vec3(0.07, 0.12, 0.26);
    vec2 cq = mq / vec2(1.0 / 6.0, depth / 10.0);
    if (!mono) {
        p *= 0.85 + 0.3 * hash(floor(cq) + cell * 7.0); // the crystals of each cell
    }
    vec2 cf = fract(cq);
    if (cf.x < 0.05 || cf.y < 0.05) {
        p = mix(p, mono ? vec3(0.1) : vec3(0.33, 0.35, 0.37), fine_detail(0.03, px) * 0.7);
    }
    // ageing: yellowed encapsulant, dust where rain runs off the low edge, droppings, cracks
    p = mix(p, vec3(0.32, 0.30, 0.22), age * 0.3 * hash(cell + 2.0));
    p = mix(p, vec3(0.42, 0.38, 0.30), age * 0.6 * pow(1.0 - r / depth, 6.0));
    if (hash(floor(mq * 12.0) + cell) > 1.0 - 0.015 * age) {
        p = mix(p, vec3(0.85, 0.85, 0.8), fine_detail(0.08, px));
    }
    if (mh > 1.0 - 0.08 * age) {
        vec2 dd = mq - vec2(hash(cell + 4.0), hash(cell + 5.0) * depth);
        float spoke = abs(fract(atan(dd.y, dd.x) * 7.0 / 6.2832 + hash(cell + 6.0)) - 0.5);
        if (spoke < 0.03 && length(dd) < 0.5) {
            p = mix(p, vec3(0.7, 0.72, 0.75), fine_detail(0.03, px)); // a shattered pane
        }
    }
    if (mq.x < 0.025 || mq.x > 0.975 || mq.y < 0.03 || mq.y > depth - 0.03) {
        p = vec3(0.62, 0.64, 0.66) * (1.0 - 0.3 * age); // the aluminium frame
    }
    // tilted 20 degrees toward the south: the sun shades it as a panel, not as the roof
    n = normalize(mix(n, normalize(vec3(0.0, 0.94, 0.0) + across * sg * 0.34), near));
    glass = near;
    return mix(avg, p, near);
}

varying float garage_flag;  // UV2.y: 0 plain, 1 neon sign, 2 garage wall (storey_count = eave m),
                            // 3 a wall run too short for a window (bands and plinth only),
                            // 4-23 a castle wall (storey_count = the run's height m; +1 a level head,
                            // +2 its foot on the ground, +4 x its variant; wall_frame.z the run's length)

// --- castles (#859) ---------------------------------------------------------------------
// A castle's wall, drawn after photos of Chillon: v is metres above its wall run's foot, h the
// run's height. Rubble courses of uneven stones, patches and rain streaks, a dark damp foot where
// the wall meets the ground or the lake, two-light Gothic windows and small square ones in rows on
// the upper floors, arrow slits below, and under a level wall head an arched corbel table.

// smooth value noise, for the weathering
float castle_noise(vec2 p) {
    vec2 i = floor(p), f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), f.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), f.x), f.y);
}

// A two-light Gothic window, q from its bottom middle (across, up), w x h inside its frame:
// 0 wall, 1 the stone frame and mullion, 2 glass. Each light ends in a pointed (equilateral) head.
int gothic_window(vec2 q, float w, float h) {
    float fw = 0.13;
    if (abs(q.x) > w * 0.5 + fw || q.y < -fw || q.y > h + fw) return 0;
    if (q.y < 0.0 || abs(q.x) < 0.05 || abs(q.x) > w * 0.5) return 1;
    float a = (w * 0.5 - 0.05) * 0.5;               // half a light's width
    float lx = abs(q.x) - 0.05 - a;                  // from that light's middle
    float spring = h - 1.732 * a;                    // where its pointed head starts
    if (q.y < spring) return 2;
    vec2 d = vec2(lx, q.y - spring);
    return (length(d - vec2(a, 0.0)) < 2.0 * a && length(d + vec2(a, 0.0)) < 2.0 * a) ? 2 : 1;
}

// a dressed stone in linear albedo: a rendered castle's quoins, window surrounds and plinth
// (the plateau's grey-green molasse sandstone)
const vec3 castle_dressing = vec3(0.34, 0.35, 0.27);

// Openings, q from the wall run's start and foot. code: 0 wall, 1 the stone frame (and a window's
// mullion, transom or glazing bars), 2 glass, 3 a slit or an empty opening, 4 a shutter.

// a cross-mullioned (Kreuzstock) window, q from its bottom middle, w x h inside its frame
int cross_window(vec2 q, float w, float h) {
    float fw = 0.13;
    if (abs(q.x) > w * 0.5 + fw || q.y < -fw || q.y > h + fw) return 0;
    if (abs(q.x) > w * 0.5 || q.y < 0.0 || q.y > h) return 1;
    if (abs(q.x) < 0.045 || abs(q.y - h * 0.62) < 0.045) return 1;
    return 2;
}

// a stately home's sash window with painted shutters folded back: w x h, the panes in thirds
int manor_window(vec2 q, float w, float h) {
    float fw = 0.14;
    if (abs(q.x) > w + fw || q.y < -fw || q.y > h + fw) return 0;
    if (abs(q.x) > w * 0.5 + fw) return q.y >= 0.0 && q.y <= h ? 4 : 0; // the shutters, one each side
    if (abs(q.x) > w * 0.5 || q.y < 0.0 || q.y > h) return 1;
    if (abs(q.x) < 0.025 || abs(q.y - h / 3.0) < 0.025 || abs(q.y - h * 2.0 / 3.0) < 0.025) return 1;
    return 2;
}

// Variants (BuildingMeshBuilder.CastleRubble..CastleTimbered): 0 coursed rubble, Gothic windows and
// an arched corbel table; 1 whitewashed render, sandstone quoins, cross-mullioned windows; 2 a
// stately home: render, quoins, a cornice, symmetric rows of shuttered windows; 3 a ruin: mossy
// rubble, empty openings; 4 render under a half-timbered top storey (eastern Switzerland); 5 brick
// (Vufflens): stretcher bond, cross windows framed in pale stone, a deep machicolated corbel table.
vec3 castle_wall(vec3 col, vec2 q, float h, float len, bool level, bool foot, int variant, vec2 seed,
                 float paint_pick, float px, inout vec3 glow) {
    bool rendered = variant == 1 || variant == 2 || variant == 4;
    bool brick = variant == 5;
    if (brick) {
        // stretcher bond: 0.30 x 0.085 m bricks, every course offset by half, light lime joints,
        // each brick its own firing (some dark, burnt); gone into an even red under a few pixels
        float row = floor(q.y / 0.085);
        float bx = q.x / 0.30 + 0.5 * mod(row, 2.0);
        vec2 ib = vec2(fract(bx) * 0.30, fract(q.y / 0.085) * 0.085);
        float joint = min(min(ib.x, 0.30 - ib.x), min(ib.y, 0.085 - ib.y));
        float fired = hash(vec2(floor(bx), row) + seed);
        col *= mix(1.0, (fired < 0.12 ? 0.62 : 0.86 + 0.26 * fired), fine_detail(0.085, px));
        col = mix(col, vec3(0.42, 0.38, 0.32), step(joint, 0.009) * fine_detail(0.02, px));
        col *= mix(0.88, 1.06, castle_noise(q * vec2(0.22, 0.3) + seed));
        float streak = castle_noise(vec2(q.x * 1.4, q.y * 0.07) + seed * 3.0);
        col *= 1.0 - 0.10 * smoothstep(0.55, 0.92, streak) * smoothstep(0.0, 6.0, q.y);
    } else if (rendered) {
        // lime render: soft patches, faint streaks; dressed stone at the corners and the plinth
        col *= mix(0.94, 1.04, castle_noise(q * vec2(0.3, 0.4) + seed));
        float streak = castle_noise(vec2(q.x * 1.4, q.y * 0.07) + seed * 3.0);
        col *= 1.0 - 0.07 * smoothstep(0.6, 0.92, streak) * smoothstep(0.0, 6.0, q.y);
        float course = floor(q.y / 0.5);
        float qw = mod(course, 2.0) < 1.0 ? 0.95 : 0.55; // long and short quoins in turn
        bool quoin = len > 3.0 && (q.x < qw || q.x > len - qw) && q.y < h - 0.2;
        bool plinth = foot && q.y < 1.0;
        if (quoin || plinth) {
            float jy = min(fract(q.y / 0.5), 1.0 - fract(q.y / 0.5)) * 0.5;
            float jx = min(abs(q.x - qw), abs(q.x - (len - qw)));
            col = castle_dressing * (1.0 + (hash(vec2(course, floor(q.x / 0.9)) + seed) - 0.5) * 0.12);
            col *= mix(1.0, 0.75, step(min(jy, jx), 0.025) * fine_detail(0.03, px));
        }
    } else {
        // rubble: 0.42 m courses, each its own stone length and offset, every stone its own tone
        float row = floor(q.y / 0.42);
        float slen = 0.55 + 0.8 * hash(vec2(row, 1.3) + seed);
        float sx = q.x / slen + hash(vec2(row, 5.1) + seed);
        vec2 in_stone = vec2(fract(sx) * slen, fract(q.y / 0.42) * 0.42);
        float joint = min(min(in_stone.x, slen - in_stone.x), min(in_stone.y, 0.42 - in_stone.y));
        col *= 1.0 + (hash(vec2(floor(sx), row) + seed) - 0.5) * 0.24 * fine_detail(0.4, px);
        col *= mix(1.0, 0.72, step(joint, 0.03) * fine_detail(0.03, px));
        // weathering: broad patches, then rain streaks running down from the head
        col *= mix(0.84, 1.08, castle_noise(q * vec2(0.22, 0.3) + seed));
        float streak = castle_noise(vec2(q.x * 1.4, q.y * 0.07) + seed * 3.0);
        col *= 1.0 - 0.14 * smoothstep(0.55, 0.92, streak) * smoothstep(0.0, 6.0, q.y);
        if (variant == 3) {
            // a ruin: moss and lichen in patches, darker toward its broken head
            float moss = smoothstep(0.45, 0.8, castle_noise(q * 0.45 + seed * 2.0));
            col = mix(col, col * vec3(0.55, 0.68, 0.42), 0.5 * moss);
            col *= mix(1.0, 0.82, smoothstep(h - 3.0, h, q.y));
        }
    }
    // the foot: damp and dark where it stands in the ground or the lake, green on stone
    if (foot) {
        // the run starts below the ground or the water line, so the band reaches well up
        float fh = 2.6 + 1.2 * castle_noise(vec2(q.x * 0.35, 0.5) + seed);
        float damp = 1.0 - smoothstep(fh - 0.7, fh, q.y);
        col = mix(col, col * (rendered ? vec3(0.62, 0.62, 0.58) : vec3(0.40, 0.45, 0.38)), damp);
    }

    // --- openings ------------------------------------------------------------------------
    int open = 0;
    bool lit = false;
    float rows_from = h;
    bool timbered = variant == 4 && level && h > 7.0;
    float band = timbered ? h - 3.0 : h; // the half-timbered storey's sill
    if (variant == 2) {
        // a stately home: storeys of 3.5 m, windows 2.9 m apart, symmetric about the wall's middle,
        // none too close to the quoins
        for (int k = 0; k < 4; k++) {
            float y0 = 1.1 + float(k) * 3.5;
            // a gable's storeys stop under its slopes, a level wall's under its cornice
            if (y0 + 1.8 > (level ? h - 0.8 : h * 0.7)) break;
            float cu = (q.x - len * 0.5) / 2.9 + 0.5;
            float cx = floor(cu) * 2.9 + len * 0.5; // this column's middle along the run
            if (cx < 1.4 || cx > len - 1.4) continue;
            int w = manor_window(vec2(q.x - cx, q.y - y0), 1.0, 1.8);
            if (w > 0) {
                open = w;
                lit = hash(vec2(floor(cu), float(k) + 7.7) + seed) > mix(0.93, 0.45, world_night);
            }
        }
    } else {
        if (h > 9.0) {
            // up to two rows of windows, 3.8 m apart, the top one 3.4 m under the head (or the band)
            for (int k = 0; k < 2; k++) {
                float y0 = band - 3.4 - float(k) * 3.8;
                if (y0 < max(4.0, h * 0.4)) break;
                rows_from = y0;
                float shift = (hash(vec2(float(k), 9.1) + seed) - 0.5) * 1.6;
                float cu = (q.x + shift) / 3.6;
                float r = hash(vec2(floor(cu), float(k) + 2.0) + seed);
                vec2 wq = vec2((fract(cu) - 0.5) * 3.6, q.y - y0);
                int w = 0;
                if (r < 0.42) {
                    w = variant == 0 || variant == 3 ? gothic_window(wq, 1.1, 1.7) : cross_window(wq, 1.0, 1.5);
                } else if (r < 0.62) {
                    // a small square window
                    w = abs(wq.x) < 0.27 && wq.y > 0.4 && wq.y < 1.1 ? 2
                      : abs(wq.x) < 0.38 && wq.y > 0.29 && wq.y < 1.21 ? 1 : 0;
                }
                if (w > 0) {
                    open = w;
                    lit = hash(vec2(floor(cu), float(k) + 7.7) + seed) > mix(0.93, 0.45, world_night);
                }
            }
        }
        if (open == 0 && h > 4.5 && q.y < band) {
            // arrow slits under the windows, their foot a third of the way up
            float cu = q.x / 5.2;
            float lu = (fract(cu) - 0.5) * 5.2;
            float r = hash(vec2(floor(cu), 0.0) + seed);
            float y0 = clamp(min(h * 0.33, rows_from - 3.0), 1.8, h - 2.4);
            if (r < 0.5) {
                bool slit = abs(lu) < 0.13 && q.y > y0 && q.y < y0 + 1.7;
                bool bar = r < 0.2 && abs(lu) < 0.4 && abs(q.y - (y0 + 0.95)) < 0.1;
                if (slit || bar) {
                    open = 3;
                } else if (abs(lu) < 0.24 && q.y > y0 - 0.1 && q.y < y0 + 1.8) {
                    col *= mix(1.0, 1.1, fine_detail(0.08, px)); // the splayed reveal
                }
            }
        }
    }

    // the half-timbered storey: posts every 1.3 m, sill and head rails, a St Andrew's cross under
    // some windows, small windows between the posts above the sill rail
    if (timbered && q.y > band) {
        float yb = q.y - band;
        float cu = q.x / 1.3;
        float pu = fract(cu) * 1.3;
        float r = hash(vec2(floor(cu), 3.3) + seed);
        bool beam = pu < 0.18 || yb < 0.2 || abs(yb - 1.0) < 0.08 || yb > 2.8;
        if (!beam && yb < 1.0) {
            vec2 d = vec2((pu - 0.18) / 1.12, (yb - 0.2) / 0.8); // the panel, 0..1 both ways
            beam = r < 0.5 && (abs(d.x - d.y) < 0.1 || abs(d.x + d.y - 1.0) < 0.1);
        }
        if (beam) {
            col = mix(col, vec3(0.075, 0.045, 0.025), fine_detail(0.12, px));
            open = 0;
        } else if (yb > 1.25 && yb < 2.45 && pu > 0.42 && pu < 1.06 && r > 0.35) {
            open = 2;
            lit = hash(vec2(floor(cu), 4.4) + seed) > mix(0.93, 0.45, world_night);
        }
    }

    if (open == 1) {
        // the frame: dressed stone round a rubble wall's glass, sandstone round a rendered one's
        // (brick windows are framed in pale molasse, as at Vufflens)
        col = rendered ? mix(col, castle_dressing * 1.1, fine_detail(0.1, px))
            : brick ? mix(col, vec3(0.45, 0.43, 0.37), fine_detail(0.1, px))
            : mix(col, col * 1.16, fine_detail(0.1, px));
    } else if (open == 4) {
        // shutters, one paint a castle (green, grey-blue, ox-blood: COLOR.a), slats across them
        float pick = paint_pick;
        vec3 paint = pick < 0.45 ? vec3(0.03, 0.10, 0.04) : pick < 0.75 ? vec3(0.09, 0.15, 0.19) : vec3(0.20, 0.03, 0.02);
        paint *= mix(1.0, 0.75, step(0.5, fract(q.y / 0.09)) * fine_detail(0.05, px));
        col = mix(col, paint, fine_detail(0.25, px));
    } else if (open >= 2) {
        float legible = fine_detail(open == 3 ? 0.26 : 0.5, px);
        vec3 dark = vec3(0.05, 0.05, 0.06);
        // a ruin's windows are holes: no glass, never lit
        if (open == 2 && variant != 3) {
            dark = lit ? window_lit : window_color * 0.8;
            if (lit) glow = window_lit * legible;
        }
        col = mix(col, dark, legible);
    }

    // under a level head: a rubble fortress's arched corbel table (corbels, round arches, the dark
    // machicolation behind them, the parapet proud above); a stately home's moulded cornice
    if (level && h > 6.0 && (variant == 0 || variant == 5)) {
        float fb = fine_detail(0.2, px);
        // Vufflens's machicolations are deeper: taller arches over longer corbels
        float bh = variant == 5 ? 1.3 : 0.8;
        float yb = q.y - (h - 0.45 - bh);
        if (yb > 0.0 && yb < bh) {
            float k = fract(q.x / 0.85) * 0.85;
            float a = 0.3, cx = 0.25 + a, spring = bh - a - 0.06;
            bool corbel = k < 0.25;
            bool hole = !corbel && (yb < spring || length(vec2(k - cx, yb - spring)) < a);
            col *= corbel ? mix(1.0, 1.08, fb) : hole ? mix(1.0, 0.32, fb) : mix(1.0, 1.02, fb);
        } else if (yb <= 0.0 && yb > -0.16) {
            col *= mix(1.0, 0.76, fb); // the corbels' shadow on the wall
        } else if (yb >= bh) {
            col *= mix(1.0, 1.05, fb);
        }
    } else if (level && variant == 2) {
        float fb = fine_detail(0.12, px);
        if (q.y > h - 0.45 && q.y < h - 0.12) {
            col = mix(col, castle_dressing * 1.25, fb);
        } else if (q.y > h - 0.6 && q.y <= h - 0.45) {
            col *= mix(1.0, 0.72, fb);
        }
    }
    return col;
}

void vertex() {
    world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    facade = UV;
    wall_frame = CUSTOM0.xyz;
    roof_kind = CUSTOM0.w;
    roof_uv = vec2(UV.x, UV2.x);
    roof_len = UV2.y;
    // a roof's UV2 is its own frame, not a storey count or a garage flag
    storey_count = roof_kind > 0.5 ? 0.0 : UV2.x;
    garage_flag = roof_kind > 0.5 ? 0.0 : UV2.y;
    POSITION = retro_snap(PROJECTION_MATRIX * MODELVIEW_MATRIX * vec4(VERTEX, 1.0));
}

void fragment() {
    if (portal_clipped(world_pos, CAMERA_POSITION_WORLD) || open_door_leaf(world_pos)) {
        discard;
    }
    vec3 n = facet_normal(VERTEX, INV_VIEW_MATRIX);
    // faces are not consistently wound in the source TINs, so orient toward the viewer
    if (!FRONT_FACING) {
        n = -n;
    }

    vec3 col = COLOR.rgb;
#ifdef STYLE_REAL
    // walls: plaster in facade space (metres along, storeys times their height); roofs: tiles
    // in plan. Windows, signs and paint are drawn over it below.
    if (roof_kind > 0.5 && roof_kind < 1.5) {
        col = style_tinted(tex_roof, roof_uv / 2.5, col, 0.25); // a pitched roof: courses along its eave
    } else if (n.y > 0.25 || facade.y < 0.0) {
        col = style_tinted(tex_roof, vec2(pattern_xz(world_pos).x + world_pos.y * 0.7, pattern_xz(world_pos).y) / 2.5, col, 0.25);
    } else if (garage_flag > 3.5) {
        col = style_tinted(tex_plaster, facade / 3.0, col, 0.15); // a castle wall: v is already metres
    } else {
        col = style_tinted(tex_plaster, vec2(facade.x, facade.y * max(wall_frame.z, 2.8)) / 3.0, col, 0.15);
    }
#endif
    // how much of this pixel is glass (windows, solar panels): glossy in the lit styles
    float glass = 0.0;
    float roof_px = max(fwidth(roof_uv.x), fwidth(roof_uv.y));
    if (roof_kind > 1.5) {
        col = solar_field(col, n, glass, roof_uv, wall_frame.xy, wall_frame.z, roof_px);
    } else if (roof_kind > 0.5) {
        col = roof_tiles(col, n, normalize(wall_frame), roof_uv, roof_len, roof_px);
    }
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
    bool garage_wall = garage_flag > 1.5 && garage_flag < 2.5;
    bool castle = garage_flag > 3.5 && roof_kind < 0.5;
    if (castle && facade.y >= 0.0) {
        float cpx = max(fwidth(facade.x), detail);
        // the wall's seed, rounded: the interpolated tangent jitters in its last bits, and the hash
        // turns that into speckle
        vec2 seed = floor(wall_frame.xy * 17.0 + 0.5) + vec2(floor(storey_count + 0.5));
        // UV2.y - 4 = level head + 2 foot on the ground + 4 x the variant; CUSTOM0.z the run's length
        float bits = garage_flag - 4.0;
        float variant = floor(bits / 4.0 + 0.01);
        float rest = bits - variant * 4.0;
        col = castle_wall(col, facade, storey_count, wall_frame.z, mod(rest, 2.0) > 0.5, rest > 1.5, int(variant),
            seed, COLOR.a, cpx, glow);
    } else if (!garage_wall && facade.y >= 0.0 && facade.y < top && detail < 0.6) {
        float u = facade.x;
        float wall_v = fract(facade.y);
        float col_u = fract(u / window_spacing);
        bool in_row = wall_v > 0.32 && wall_v < 0.76;
        bool in_col = col_u > 0.30 && col_u < 0.70;
        vec2 cell = vec2(floor(u / window_spacing), floor(facade.y));
        // fade the pattern out as it approaches the resolution limit
        float legible = clamp(1.0 - detail / 0.6, 0.0, 1.0);
        float storey_h = max(wall_frame.z, 0.5);
        float px = detail * storey_h; // metres a pixel spans on this wall

        // the wall's frame: along (u grows that way), out toward the viewer, and the sun in it
        vec3 along = normalize(vec3(wall_frame.x, 0.0, wall_frame.y));
        vec3 out_n = vec3(-along.z, 0.0, along.x);
        if (dot(out_n, n) < 0.0) {
            out_n = -out_n;
        }
        vec3 sun = normalize(world_sun_dir);
        vec3 sun_f = vec3(dot(sun, along), sun.y, dot(sun, out_n));
        // the opening in this window cell, metres (along, up the storey)
        vec2 lo = vec2(0.30 * window_spacing, 0.32 * storey_h);
        vec2 hi = vec2(0.70 * window_spacing, 0.76 * storey_h);
        vec2 q = vec2(col_u * window_spacing, wall_v * storey_h);
        bool windowed = facade.y > 0.35 && garage_flag < 2.5;

        // shutters on some houses, not on blocks of flats or a church: one paint a house
        float house = hash(floor(pattern_xz(world_pos) / 24.0) + 0.37);
        bool shuttered = house < 0.55 && storey_h < 3.6 && storey_count < 5.5;
        vec3 paint = house < 0.2 ? vec3(0.17, 0.34, 0.21) : house < 0.32 ? vec3(0.46, 0.12, 0.10)
                   : house < 0.44 ? vec3(0.36, 0.24, 0.14) : vec3(0.36, 0.43, 0.47);
        float leaf_w = (hi.x - lo.x) * 0.5;
        float slats = fine_detail(0.075, px);

        if (windowed && in_row && in_col) {
            // stable per-window hash decides which ones are lit
            float r = hash(cell);
            // someone inside lights more of them
            float busy = occupancy(world_pos);
            // a few lit windows by day, most of a village at night
            bool lit = r > mix(0.86, 0.35 - 0.2 * world_lights, world_night) - busy * 0.45;
            // a shuttered house keeps a few of its dark windows shut
            bool closed = shuttered && !lit && busy == 0.0 && hash(cell + 6.6) < 0.15;
            vec3 win = lit ? window_lit : window_color;

            // into the recess: the glass, or a reveal in front of it
            vec3 ray = normalize(world_pos - CAMERA_POSITION_WORLD);
            vec3 d = vec3(dot(ray, along), ray.y, -dot(ray, out_n));
            int side;
            float th = recess_trace(q, d, lo, hi, window_recess * legible, side);
            vec2 gq = q + d.xy * th;
            float shade = mix(0.55, 1.0, recess_sun(gq, d.z * th, sun_f, lo, hi));

            if (side == 0) {
                // close enough to see into: a room behind it (a church's tall storey gets coloured glass)
                if (closed) {
                    float lx = gq.x - lo.x;
                    win = shutter(vec2(mod(lx, leaf_w), gq.y - lo.y), leaf_w, hi.y - lo.y, paint, slats);
                } else if (legible > 0.05 && wall_frame.z > 0.0) {
                    if (storey_h > 5.5) {
                        win = stained_glass(gq, cell, lit);
                    } else {
                        win = room_behind(world_pos + ray * th, CAMERA_POSITION_WORLD, wall_frame.xy, storey_h,
                            gq.x / window_spacing, gq.y / storey_h, cell, lit, busy, TIME);
                    }
                }
                col = mix(col, win * (lit ? 1.0 : shade), legible);
                glass = closed ? 0.0 : legible;
                if (lit) glow = win * legible;
            } else {
                // a reveal: the wall turned into the opening, its own facing for the sun, darker
                // toward the glass; a lit room spills onto it at night
                vec3 rn = side == 1 ? -along * sign(d.x) : vec3(0.0, side == 2 ? -1.0 : 1.0, 0.0);
                n = normalize(mix(n, rn, legible));
                float ao = mix(1.0, 0.62, clamp(d.z * th / window_recess, 0.0, 1.0));
                col *= mix(1.0, ao * shade, legible);
                if (lit) glow = window_lit * 0.3 * legible;
            }
        } else if (windowed) {
            // --- around the opening: shutters folded back, the sill and its shadow ---------
            float sill_lo = lo.y - 0.07;
            bool beside = in_row && col_u > 0.30 - leaf_w / window_spacing && col_u < 0.70 + leaf_w / window_spacing;
            if (shuttered && beside) {
                float lx = q.x < lo.x ? q.x - (lo.x - leaf_w) : q.x - hi.x;
                col = mix(col, shutter(vec2(lx, q.y - lo.y), leaf_w, hi.y - lo.y, paint, slats), legible);
            } else if (q.x > lo.x - 0.06 && q.x < hi.x + 0.06 && q.y < lo.y && q.y > sill_lo) {
                // the stone sill's front, standing out of the wall
                col = mix(col, mix(col, vec3(0.80, 0.78, 0.74), 0.55) * 1.08, fine_detail(0.07, px) * legible);
            } else if (q.x > lo.x - 0.06 && q.x < hi.x + 0.06 && q.y < sill_lo && q.y > sill_lo - 0.08) {
                // the sill's shadow on the wall below, deepest right under it
                float under = (sill_lo - q.y) / 0.08;
                col *= mix(1.0, mix(0.68, 1.0, under), fine_detail(0.05, px) * legible);
            }
        }

        // --- string courses and the plinth --------------------------------------------------
        float bands = legible * fine_detail(0.06, px);
        if (storey_count > 1.5 && cell.y >= 1.0 && q.y < 0.14) {
            col *= mix(1.0, 1.08, bands); // the slab's edge, a band proud of the wall
        } else if (storey_count > 1.5 && cell.y + 1.0 < storey_count - 0.5 && q.y > storey_h - 0.05) {
            col *= mix(1.0, 0.8, bands); // the shadow line under it
        }
        float base_m = facade.y * storey_h;
        if (base_m < plinth_height) {
            col *= mix(1.0, 0.82, bands); // a plinth of rougher, darker render
        } else if (base_m < plinth_height + 0.04) {
            col *= mix(1.0, 1.1, bands); // its chamfered top catches the light
        }
    }

    // --- festive lights (a running occasion's world_lights) ---------------------------
    // A string of bulbs along the eaves, just under the wall plate, on about 60% of the houses
    // that have a window grid. Drawn here rather than as geometry: every house in view gets them
    // for the cost of a few instructions, and they follow the facade frame the windows use.
    if (!garage_wall && !castle && world_lights > 0.0 && storey_count > 0.5 && detail < 0.6) {
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

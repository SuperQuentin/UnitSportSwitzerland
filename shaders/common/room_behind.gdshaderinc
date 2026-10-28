// The fake room behind a pane (#59), shared by the buildings' window grid and a shop's glazed front (#1292).
// The includer declares hash(), and the uniforms window_color, window_lit and room_depth; world_night and
// world_sky come from world.gdshaderinc. ROOM_SHOP, defined before the include, leaves out a home's curtains
// and the window's own mullion and transom: a shop's glass is clear and framed by its front.

// --- interior mapping ------------------------------------------------------------------
// A room behind every window, without any geometry: the view ray enters at the glass and is
// followed to the first of the room's planes, in the wall's own frame - u along the facade
// (one room per window column), v up the storey, d into the building. The room's colours,
// depth and furniture come from the window's hash, so a house reads the same every visit.
// lu01 / lv01 are where the ray enters, as a fraction of the cell (w metres wide) and the storey.
vec3 room_behind(vec3 p, vec3 eye, vec2 tangent, float w, float h, float lu01, float lv01, vec2 cell,
                 bool lit, float busy, float time) {
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
#ifndef ROOM_SHOP
    float drape = hash(cell + 8.8);
    if (drape < 0.5) {
        // a sheer net curtain across the whole pane: a figure behind a lit one still shows
        vec3 net = lit ? window_lit * 0.95 : vec3(0.78, 0.78, 0.74) * mix(1.0, 0.12, world_night);
        c = mix(c, net, 0.65);
    } else if (lu01 < 0.36 || lu01 > 0.64) {
        vec3 cloth = drape < 0.67 ? vec3(0.45, 0.14, 0.12) : drape < 0.84 ? vec3(0.22, 0.34, 0.24) : vec3(0.70, 0.62, 0.48);
        c = cloth * (lit ? window_lit * 0.8 : mix(vec3(0.6), vec3(0.08), world_night));
    }
#endif
    float fres = 0.35 + 0.4 * pow(1.0 - dd, 2.0);
    vec3 glass = mix(window_color, world_sky, 0.55);
    c = mix(c, glass, lit ? fres * 0.35 : fres);
#ifndef ROOM_SHOP
    // a mullion and a transom
    if (abs(lu01 - 0.5) < 0.012 || abs(lv01 - 0.62) < 0.012) {
        c = vec3(0.86, 0.85, 0.82) * (lit ? 0.7 : 1.0);
    }
#endif
    return c;
}


#!/usr/bin/env python3
"""The shot queues of tools/refshots.sh (#1429; --frame earth for G2 C6, #1500): one per launch.

    tools/refshot-poses.py VIEWS_TXT OUT_DIR PNG_DIR [--frame flat|earth] [--anchors] [--heights LIB/STYLE]
                           [--views "a b"] [--settle S] [--hour H] [--date D]

Reads the catalogue (name class E N height pitch bearing [hour] [date]) and writes OUT_DIR/q_<n>.txt, one queue per
launch, and prints the launches, one a line: "<queue> <date> <anchorE> <anchorN> <shots>".

flat, no --anchors: one launch per date, every view relative to the catalogue's first view's whole km (the library
    format of #1429, unchanged: x = E - oe, z = on - N, yaw = -bearing).
--anchors (the flat side of an earth comparison) and --frame earth: one launch per date and anchor. Views are
    gathered round anchors on a whole km, each view within ANCHOR_REACH of its anchor, so the start frame's
    vertical is the view's own (6 km: 0.05 deg, under a pixel at the frame's edge). The game starts with
    --at and --origin on the anchor.
--frame earth: the earth world is the east-up-south frame at the anchor (ClientWorld: FrameAxes.Enu at
    SwissGeodesy.ToEcef(atE, atN, 0)). Each view's position goes LV95 + LHN95 height -> ECEF -> that frame, and its
    direction too (a point 1 km along its grid bearing and pitch, through the same maths), so the earth shot is
    the same place looking the same way as the flat one: its direction is taken in the flat world's own axes at the
    place (FrameAxes.GroundAt: grid east, the ellipsoid's up, grid north) and turned into the anchor's, so the
    grid's convergence (~0.9 deg at Geneva) and the Earth's curvature are in the numbers, not in the picture.
--heights LIB/STYLE: the flat library's logs of that style ("[shot] wrote <view>.png ... at (x, y, z)"): a "g" view
    is taken at the altitude the flat shot stood at (y is the LHN95 altitude in the flat ground frame), so both
    worlds stand at the same point even where their grounds differ. Without it a "g" view keeps its "g" (above
    the earth ground under it).
The maths are tools/TerrainFormat/SwissGeodesy.cs and src/Core/WorldOrigin.cs (FrameAxes.Enu), ported.
"""
import math
import os
import re
import sys

ANCHOR_REACH = 6000.0  # m from its anchor a view may be

# --- SwissGeodesy (tools/TerrainFormat/SwissGeodesy.cs) ---------------------------------------------------------
BESSEL_A, BESSEL_F = 6377397.155, 1 / 299.1528128
WGS_A, WGS_F = 6378137.0, 1 / 298.257223563
BE2 = BESSEL_F * (2 - BESSEL_F)
BE = math.sqrt(BE2)
BEP2 = BE2 / (1 - BE2)
DATUM = (674.374, 15.056, 405.346)
PHI0 = math.radians(46 + 57 / 60 + 8.66 / 3600)
LAMBDA0 = math.radians(7 + 26 / 60 + 22.50 / 3600)
R = BESSEL_A * math.sqrt(1 - BE2) / (1 - BE2 * math.sin(PHI0) ** 2)
ALPHA = math.sqrt(1 + BEP2 * math.cos(PHI0) ** 4)
B0 = math.asin(math.sin(PHI0) / ALPHA)
K = (math.log(math.tan(math.pi / 4 + B0 / 2)) - ALPHA * math.log(math.tan(math.pi / 4 + PHI0 / 2))
     + ALPHA * BE / 2 * math.log((1 + BE * math.sin(PHI0)) / (1 - BE * math.sin(PHI0))))
GEOID_MIN_E, GEOID_MIN_N, GEOID_STEP, GEOID_COLS, GEOID_ROWS = 2_460_000, 1_060_000, 20_000, 21, 13
GEOID_CM = [  # swisstopo CHGeo2004 above Bessel, as SwissGeodesy.GeoidCm
    -483, -438, -335, -243, -69, 35, 42, 54, 27, 17, -181, -417, -549, -599, -596, -561, -513, -433, -313, -248, -149,
    -410, -405, -338, -194, -89, 60, 131, 204, 230, 172, 90, -122, -284, -331, -317, -266, -214, -165, -62, 7, 27,
    -333, -341, -291, -202, -87, 39, 71, 222, 288, 260, 125, 117, -71, -112, -138, -35, 15, 72, 117, 201, 171,
    -287, -234, -260, -183, -74, -29, 41, 78, 186, 197, 154, 163, 89, 29, 39, 70, 155, 206, 237, 332, 303,
    -239, -169, -153, -187, -144, -78, 48, 109, 178, 228, 224, 214, 173, 109, 148, 217, 303, 338, 349, 401, 355,
    -206, -125, -64, -101, -123, -83, 3, 67, 88, 187, 242, 228, 210, 189, 194, 222, 306, 374, 412, 404, 403,
    -208, -124, -49, -34, -95, -99, -64, -22, -10, 64, 122, 182, 171, 156, 121, 159, 267, 370, 428, 423, 497,
    -218, -130, -50, -2, 3, -59, -70, -63, -34, -1, 15, 33, 95, 120, 147, 98, 216, 356, 454, 465, 573,
    -193, -135, -68, -4, 39, 46, 13, -27, -27, -32, -46, -46, 7, 41, 72, 98, 197, 330, 413, 433, 527,
    -175, -124, -72, -15, 31, 62, 68, 56, 35, 7, -14, -28, -27, 17, 60, 53, 182, 299, 390, 446, 452,
    -160, -121, -70, -14, 35, 61, 78, 86, 94, 89, 56, 34, 21, 24, 30, 43, 121, 235, 325, 387, 423,
    -135, -105, -56, 15, 82, 116, 115, 128, 176, 217, 181, 140, 101, 77, 59, 68, 121, 204, 262, 300, 345,
    -94, -62, -19, 46, 140, 205, 187, 163, 226, 314, 301, 262, 222, 177, 150, 140, 173, 215, 254, 278, 297,
]


def bessel_geoid(e, n):
    gx = min(max((e - GEOID_MIN_E) / GEOID_STEP, 0), GEOID_COLS - 1)
    gy = min(max((n - GEOID_MIN_N) / GEOID_STEP, 0), GEOID_ROWS - 1)
    x0, y0 = min(int(gx), GEOID_COLS - 2), min(int(gy), GEOID_ROWS - 2)
    fx, fy = gx - x0, gy - y0

    def at(x, y):
        return GEOID_CM[y * GEOID_COLS + x]
    south = at(x0, y0) + (at(x0 + 1, y0) - at(x0, y0)) * fx
    north = at(x0, y0 + 1) + (at(x0 + 1, y0 + 1) - at(x0, y0 + 1)) * fx
    return (south + (north - south) * fy) / 100.0


def to_bessel(e, n):
    y, x = e - 2_600_000.0, n - 1_200_000.0
    l_bar = y / R
    b_bar = 2 * (math.atan(math.exp(x / R)) - math.pi / 4)
    b = math.asin(math.cos(B0) * math.sin(b_bar) + math.sin(B0) * math.cos(b_bar) * math.cos(l_bar))
    l = math.atan2(math.sin(l_bar), math.cos(B0) * math.cos(l_bar) - math.sin(B0) * math.tan(b_bar))
    lon = LAMBDA0 + l / ALPHA
    lat, s0 = b, (math.log(math.tan(math.pi / 4 + b / 2)) - K) / ALPHA
    for _ in range(12):
        s = s0 + BE * math.log(math.tan(math.pi / 4 + math.asin(BE * math.sin(lat)) / 2))
        nxt = 2 * math.atan(math.exp(s)) - math.pi / 2
        done = abs(nxt - lat) < 1e-15
        lat = nxt
        if done:
            break
    return lat, lon


def ell_to_ecef(a, f, lat, lon, h):
    e2 = f * (2 - f)
    nn = a / math.sqrt(1 - e2 * math.sin(lat) ** 2)
    return ((nn + h) * math.cos(lat) * math.cos(lon), (nn + h) * math.cos(lat) * math.sin(lon),
            (nn * (1 - e2) + h) * math.sin(lat))


def ecef_to_latlon(a, f, p):
    e2 = f * (2 - f)
    x, y, z = p
    lon = math.atan2(y, x)
    r = math.hypot(x, y)
    lat = math.atan2(z, r * (1 - e2))
    for _ in range(10):
        nn = a / math.sqrt(1 - e2 * math.sin(lat) ** 2)
        h = r / math.cos(lat) - nn
        lat = math.atan2(z, r * (1 - e2 * nn / (nn + h)))
    return lat, lon


def to_ecef(e, n, h_lhn95):
    lat, lon = to_bessel(e, n)
    x, y, z = ell_to_ecef(BESSEL_A, BESSEL_F, lat, lon, h_lhn95 + bessel_geoid(e, n))
    return x + DATUM[0], y + DATUM[1], z + DATUM[2]


def enu_axes(origin):
    """FrameAxes.Enu: X east, Y the WGS84 up, Z south."""
    lat, lon = ecef_to_latlon(WGS_A, WGS_F, origin)
    east = (-math.sin(lon), math.cos(lon), 0.0)
    up = (math.cos(lat) * math.cos(lon), math.cos(lat) * math.sin(lon), math.sin(lat))
    north = (-math.sin(lat) * math.cos(lon), -math.sin(lat) * math.sin(lon), math.cos(lat))
    return origin, east, up, tuple(-c for c in north)


def in_frame(axes, p):
    o, x, y, z = axes
    d = (p[0] - o[0], p[1] - o[1], p[2] - o[2])
    dot = lambda u, v: u[0] * v[0] + u[1] * v[1] + u[2] * v[2]
    return dot(d, x), dot(d, y), dot(d, z)


def direction_in_frame(axes, v):
    _, x, y, z = axes
    dot = lambda u, w: u[0] * w[0] + u[1] * w[1] + u[2] * w[2]
    return dot(v, x), dot(v, y), dot(v, z)


def ground_axes(e, n, alt):
    """FrameAxes.GroundAt: the flat world's own axes at a place, grid east, the WGS84 up, grid north (unit, ECEF)."""
    o = to_ecef(e, n, alt)
    lat, lon = ecef_to_latlon(WGS_A, WGS_F, o)
    up = (math.cos(lat) * math.cos(lon), math.cos(lat) * math.sin(lon), math.sin(lat))
    s = to_ecef(e + 1, n, alt)
    east = tuple(s[i] - o[i] for i in range(3))
    along = sum(east[i] * up[i] for i in range(3))
    east = tuple(east[i] - up[i] * along for i in range(3))
    ln = math.sqrt(sum(c * c for c in east))
    east = tuple(c / ln for c in east)
    north = (up[1] * east[2] - up[2] * east[1], up[2] * east[0] - up[0] * east[2], up[0] * east[1] - up[1] * east[0])
    return east, up, north


# --- the catalogue ----------------------------------------------------------------------------------------------
def read_views(path, hour0, date0):
    views = []
    with open(path, encoding="utf-8") as f:
        for line in f:
            p = line.split()
            if not p or p[0].startswith("#"):
                continue
            views.append({"name": p[0], "cls": p[1], "e": float(p[2]), "n": float(p[3]), "h": p[4],
                          "pitch": p[5], "bearing": p[6], "hour": p[7] if len(p) > 7 else hour0,
                          "date": p[8] if len(p) > 8 else date0})
    return views


def flat_heights(lib_style):
    """{view: LHN95 altitude} from a flat library's logs of one style."""
    out = {}
    if not lib_style or not os.path.isdir(lib_style):
        return out
    pat = re.compile(r"\[shot\] wrote .*[/\\]([^/\\]+)\.png \(\d+x\d+\) at \(([-\d.eE]+), ([-\d.eE]+), ([-\d.eE]+)\)")
    for f in sorted(os.listdir(lib_style)):
        if not f.endswith(".log") or f == "models.log":
            continue
        with open(os.path.join(lib_style, f), encoding="utf-8", errors="replace") as fh:
            for line in fh:
                m = pat.search(line)
                if m:
                    out[m.group(1)] = float(m.group(3))
    return out


def num(s):
    """awk's "$x + 0" as the queue always printed it: an integer without its ".0"."""
    v = float(s)
    return str(int(v)) if v == int(v) else repr(v)


def main(argv):
    pos, opts, flags = [], {}, set()
    i = 1
    while i < len(argv):
        a = argv[i]
        if a == "--anchors":
            flags.add(a)
        elif a.startswith("--"):
            opts[a] = argv[i + 1]
            i += 1
        else:
            pos.append(a)
        i += 1
    if len(pos) < 3:
        print(__doc__)
        return 2
    views_txt, out_dir, png_dir = pos[:3]
    frame = opts.get("--frame", "flat")
    settle = opts.get("--settle", "8")
    views = read_views(views_txt, opts.get("--hour", "14"), opts.get("--date", "2026-07-15"))
    # the catalogue's origin, from its first view, also for a subset (#1429: from another one the floats round otherwise)
    oe, on = int(views[0]["e"] // 1000) * 1000, int(views[0]["n"] // 1000) * 1000
    if opts.get("--views"):
        want = set(opts["--views"].split())
        views = [v for v in views if v["name"] in want]
    if not views:
        print("no view matches", file=sys.stderr)
        return 2
    anchored = frame == "earth" or "--anchors" in flags

    # anchors, in catalogue order: a view joins the first anchor within reach, else starts its own on its whole km
    anchors = []
    for v in views:
        if not anchored:
            v["anchor"] = (oe, on)
            continue
        for a in anchors:
            if math.hypot(v["e"] - a[0], v["n"] - a[1]) <= ANCHOR_REACH:
                v["anchor"] = a
                break
        else:
            a = (int(v["e"] // 1000) * 1000, int(v["n"] // 1000) * 1000)
            anchors.append(a)
            v["anchor"] = a
    heights = flat_heights(opts.get("--heights", "")) if frame == "earth" else {}

    # one launch per (date, anchor), in the order each first appears
    launches = []
    for v in views:
        key = (v["date"], v["anchor"])
        if key not in launches:
            launches.append(key)
    os.makedirs(out_dir, exist_ok=True)
    for k, (date, (ae, an)) in enumerate(launches):
        lines, hour, shots = [], None, 0
        axes = enu_axes(to_ecef(ae, an, 0)) if frame == "earth" else None
        for v in views:
            if (v["date"], v["anchor"]) != (date, (ae, an)):
                continue
            if v["hour"] != hour:
                hour = v["hour"]
                hf = float(hour)
                h, m = int(hf), int((hf - int(hf)) * 60 + 0.5)
                lines.append(f"/time set {h}:{m:02d}")
            path = f"{png_dir}/{v['name']}.png"
            hs = v["h"]
            if axes is None:
                y = hs if hs[0] in "gil" else num(hs)
                lines.append(f"{int(v['e'] - ae)},{y},{int(an - v['n'])},{v['pitch']},{num(str(-float(v['bearing'])))},{settle},{path}")
                shots += 1
                continue
            # earth: the place and the place it looks at, through the Earth
            kind = hs[0] if hs[0] in "gil" else ""
            if kind == "g" and v["name"] in heights:
                alt, kind = heights[v["name"]], ""
            elif kind:
                alt = 500.0  # only x, z are used: the ground decides the height
            else:
                alt = float(hs)
            p = to_ecef(v["e"], v["n"], alt)
            pitch, bearing = math.radians(float(v["pitch"])), math.radians(float(v["bearing"]))
            east, up, north = ground_axes(v["e"], v["n"], alt)
            ch, sh = math.cos(pitch), math.sin(pitch)
            look = tuple(ch * (math.sin(bearing) * east[i] + math.cos(bearing) * north[i]) + sh * up[i] for i in range(3))
            x, y, z = in_frame(axes, p)
            dx, dy, dz = direction_in_frame(axes, look)
            # the camera looks along its -Z: yaw turns -Z toward -X (Godot's Euler YXZ), pitch up
            yaw = math.degrees(math.atan2(-dx, -dz))
            pit = math.degrees(math.atan2(dy, math.hypot(dx, dz)))
            ys = f"{kind}{hs[1:]}" if kind else f"{y:.3f}"
            lines.append(f"{x:.3f},{ys},{z:.3f},{pit:.4f},{yaw:.4f},{settle},{path}")
            shots += 1
        lines.append("quit")
        q = os.path.join(out_dir, f"q_{k:02d}_{date}_{ae}_{an}.txt")
        with open(q, "w", encoding="utf-8", newline="\n") as f:
            f.write("\n".join(lines) + "\n")
        print(f"{q} {date} {ae} {an} {shots}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))

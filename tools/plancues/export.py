"""Cue -> game formats: a house-layouts.json (HouseLayout v2) and a proposed StadiumSpec row. Nothing here edits game code."""
import difflib
import json
import math
import os
import re

import numpy as np
import shapely
from shapely.geometry import LineString, MultiPolygon, Point, Polygon
from shapely.ops import unary_union

import bldgframe
import cue as cuem
import extract as ex
import place
import tiles

ROOM_TYPES = ["Hall", "Living", "Kitchen", "Dining", "Bedroom", "Bathroom", "WC", "Office", "Storage", "Garage", "Laundry",
              "GuestRoom", "HomeCinema", "Carnotzet", "MusicRoom", "Shelter", "Cellar", "Playroom", "Study", "Pantry", "Workshop", "Corridor"]
SLACK = 0.3  # HouseLayout.FootprintSlack
GRID = 0.125
MAX_COORD, MIN_AREA, MIN_WALL, ON_WALL, MAX_CORNERS = 150.0, 1.0, 0.1, 0.03, 64
MIN_KEPT_ROOMS, MIN_KEPT_AREA = 0.5, 0.5  # a layout that keeps less than this share of the extracted rooms, or of their area, is refused


def keep_guard(kept, total, kept_area, total_area):
    """Why a layout must not be written: it keeps only a small part of what was extracted (UniS: 98 rooms became one 3 m2 sliver and
    still validated). None when enough is kept."""
    if not total or kept <= 0:
        return "no room is kept (the cue has %d)" % total
    why = []
    if kept < MIN_KEPT_ROOMS * total:
        why.append("only %d of %d extracted rooms are kept (%.0f %%, under %.0f %%)" % (kept, total, 100.0 * kept / total, 100 * MIN_KEPT_ROOMS))
    if total_area > 0 and kept_area < MIN_KEPT_AREA * total_area:
        why.append("only %.0f m2 of %.0f m2 of room area is kept (%.0f %%, under %.0f %%)" % (kept_area, total_area, 100.0 * kept_area / total_area, 100 * MIN_KEPT_AREA))
    return "; ".join(why) or None


# ------------------------------------------------------------------------------------------------ house layout
def _poly_edges(p):
    pts = list(p.exterior.coords)[:-1]
    return [(pts[i], pts[(i + 1) % len(pts)]) for i in range(len(pts))]


def on_wall(pts, x0, z0, x1, z1):
    """Edge index of the polygon (list of points) the segment lies on within 3 cm, -1 for none (HouseLayout.OnWall)."""
    for i in range(len(pts)):
        a, b = pts[i], pts[(i + 1) % len(pts)]
        dx, dz = b[0] - a[0], b[1] - a[1]
        ln = math.hypot(dx, dz)
        if ln < 1e-6:
            continue
        ux, uz = dx / ln, dz / ln

        def on(x, z):
            t = (x - a[0]) * ux + (z - a[1]) * uz
            d = abs(-(x - a[0]) * uz + (z - a[1]) * ux)
            return d <= ON_WALL and -ON_WALL <= t <= ln + ON_WALL
        if on(x0, z0) and on(x1, z1):
            return i
    return -1


def validate(rooms, openings, footprint_rings):
    """A Python mirror of HouseLayout.Validate for one ground floor (rooms: list of point lists, openings: dicts)."""
    err = []
    polys = []
    for i, pts in enumerate(rooms):
        n = len(pts)
        if n < 3 or n > MAX_CORNERS or any(abs(v) > MAX_COORD for p in pts for v in p):
            err.append("room %d: corners" % i)
            polys.append(None)
            continue
        short = min(math.dist(pts[k], pts[(k + 1) % n]) for k in range(n))
        if short < MIN_WALL:
            err.append("room %d: wall of %.0f cm" % (i, short * 100))
            polys.append(None)
            continue
        if _self_crossing(pts):
            err.append("room %d: walls cross" % i)
            polys.append(None)
            continue
        poly = Polygon(pts)
        if abs(poly.area) < MIN_AREA:
            err.append("room %d: under 1 m2" % i)
            polys.append(None)
            continue
        polys.append(poly)
        for j in range(i):
            if polys[j] is not None and poly.intersection(polys[j]).area > 0.1:
                err.append("room %d overlaps room %d (%.2f m2)" % (i, j, poly.intersection(polys[j]).area))
        if footprint_rings:
            for k in range(n):
                a, b = pts[k], pts[(k + 1) % n]
                if not _inside_outline(footprint_rings, a[0], a[1]) or not _inside_outline(footprint_rings, (a[0] + b[0]) / 2, (a[1] + b[1]) / 2):
                    err.append("room %d: outside the building" % i)
                    break
    for k, o in enumerate(openings):
        ln = math.hypot(o["x1"] - o["x0"], o["z1"] - o["z0"])
        if ln < 0.5 or ln > 3:
            err.append("door %d: width %.2f" % (k, ln))
        if not any(on_wall(p, o["x0"], o["z0"], o["x1"], o["z1"]) >= 0 for p in rooms):
            err.append("door %d: not on a wall" % k)
    if not err:
        err += _reach(rooms, openings)
    return err


def _reach(rooms, openings):
    links = {i: set() for i in range(len(rooms))}
    start = set()
    for o in openings:
        on = [i for i, p in enumerate(rooms) if on_wall(p, o["x0"], o["z0"], o["x1"], o["z1"]) >= 0]
        if len(on) >= 2:
            links[on[0]].add(on[1])
            links[on[1]].add(on[0])
        elif len(on) == 1 and o["kind"] == "door":
            start.add(on[0])
    if not start:
        return ["no front door"]
    seen, stack = set(start), list(start)
    while stack:
        for n in links[stack.pop()]:
            if n not in seen:
                seen.add(n)
                stack.append(n)
    return ["room %d: no way in" % i for i in range(len(rooms)) if i not in seen]


def _cross(a, b, c, d):
    def o(p, q, r):
        return (q[0] - p[0]) * (r[1] - p[1]) - (q[1] - p[1]) * (r[0] - p[0])
    d1, d2, d3, d4 = o(c, d, a), o(c, d, b), o(a, b, c), o(a, b, d)
    if ((d1 > 0 > d2) or (d1 < 0 < d2)) and ((d3 > 0 > d4) or (d3 < 0 < d4)):
        return True
    eps = 1e-3
    return (_segdist(a, c, d) < eps or _segdist(b, c, d) < eps or _segdist(c, a, b) < eps or _segdist(d, a, b) < eps)


def _segdist(p, a, b):
    dx, dz = b[0] - a[0], b[1] - a[1]
    l2 = dx * dx + dz * dz
    t = 0 if l2 == 0 else max(0, min(1, ((p[0] - a[0]) * dx + (p[1] - a[1]) * dz) / l2))
    return math.hypot(a[0] + t * dx - p[0], a[1] + t * dz - p[1])


def _self_crossing(pts):
    n = len(pts)
    for i in range(n):
        for j in range(i + 1, n):
            if j == i + 1 or (i == 0 and j == n - 1):
                continue
            if _cross(pts[i], pts[(i + 1) % n], pts[j], pts[(j + 1) % n]):
                return True
    return False


def _inside_outline(rings, x, z):
    inside = False
    for r in rings:
        for i in range(len(r)):
            a, b = r[i], r[i - 1]
            if (a[1] > z) != (b[1] > z) and x < (b[0] - a[0]) * (z - a[1]) / (b[1] - a[1]) + a[0]:
                inside = not inside
            if _segdist((x, z), a, b) <= SLACK:
                return True
    return inside


def _clean(g):
    """One simple polygon (largest part) from any geometry; None if nothing usable."""
    if g.is_empty:
        return None
    if g.geom_type != "Polygon":
        parts = [x for x in getattr(g, "geoms", []) if x.geom_type == "Polygon"]
        if not parts:
            return None
        g = max(parts, key=lambda x: x.area)
    g = Polygon(g.exterior)
    if not g.is_valid:
        g = g.buffer(0)
        if g.geom_type != "Polygon":
            return _clean(g)
    return g


def _type_for(room, poly):
    t = room.get("type")
    if t in ROOM_TYPES:
        return t, "label"
    w = poly.minimum_rotated_rectangle
    xs = list(w.exterior.coords)
    s1, s2 = math.dist(xs[0], xs[1]), math.dist(xs[1], xs[2])
    long, short = max(s1, s2), min(s1, s2)
    if short < 3.5 and long / max(short, 0.1) > 3.5:
        return "Corridor", "shape (long and narrow)"
    if poly.area > 120:
        return "Hall", "shape (very large open space)"
    return "Office", "default for an unlabelled room"


def house_layout(c, chunks, exit_snap=0.0, log=print):
    """(layout dict, report dict) for the cue's ground floor, or (None, report)."""
    rep = dict(left_out=[], notes=[])
    p = c.get("placement")
    if not p or not c.get("levels") or p.get("egid") is None:
        rep["notes"].append("cue has no placement on a building solid")
        return None, rep
    lvl = c["levels"][0]
    tile = tuple(int(v) for v in p["tile"].split("_"))
    b = next((x for x in tiles.buildings_near(chunks, p["e"], p["n"], 120) if x["egid"] == p["egid"]), None)
    if b is None:
        rep["notes"].append("EGID %s not found in the tiles" % p["egid"])
        return None, rep
    o = bldgframe.outline(b)
    rings = bldgframe.local_rings(o)
    foot = None
    for r in rings:
        pg = Polygon(r).buffer(0)
        foot = pg if foot is None else foot.symmetric_difference(pg)
    foot_plus = foot.buffer(SLACK - 0.05, join_style=2, mitre_limit=2.0)

    def local(pt):
        e, n = place.to_lv95(pt[0], pt[1], p["e"], p["n"], p["yaw_deg"])
        return bldgframe.to_local(o, e, n)

    cand = {}
    for i, r in enumerate(lvl["rooms"]):
        poly = Polygon([local(q) for q in r["poly"]])
        if not poly.is_valid:
            poly = poly.buffer(0)
        clipped = _clean(poly.intersection(foot_plus))
        if clipped is None or clipped.area < 1.5:
            rep["left_out"].append(dict(room=i, why="outside the building's footprint in our tile", area_m2=round(poly.area, 1)))
            continue
        cand[i] = clipped
    # simplify all rooms together (shared walls stay shared), coarser until no wall is under 12 cm and no room has too many corners
    ids = list(cand)
    final = {}
    for tol in (0.06, 0.1, 0.15, 0.22, 0.3, 0.45):
        simp = shapely.coverage_simplify([cand[i] for i in ids], tol)
        final = {}
        for i, g in zip(ids, simp):
            # corners on a 12.5 cm grid, the same for every room: shared corners stay shared and no wall is shorter than the grid
            g = _clean(shapely.set_precision(g, GRID, mode="valid_output"))
            if g is not None and g.area >= MIN_AREA:
                final[i] = g
        ok = all(len(g.exterior.coords) - 1 <= 60 and min(math.dist(a, b) for a, b in _poly_edges(g)) >= 0.12 for g in final.values())
        if ok:
            rep["notes"].append("simplified with tolerance %.2f m" % tol)
            break
    # doors from the cue: on the shared edge of the final polygons nearest the drawn door
    openings, used = [], []
    for k, op in enumerate(lvl["openings"]):
        mid = local(((op["a"][0] + op["b"][0]) / 2, (op["a"][1] + op["b"][1]) / 2))
        w = op["width_m"]
        i, j = op["between"]
        why_out = "free-space contact"
        if j is not None and ((i not in final) != (j not in final)):
            # a door between a kept room and one outside the part of the building the game's frame holds:
            # it leads out of this layout, so it is the layout's way in from the rest of the building
            i, j = (i, None) if i in final else (j, None)
            why_out = "door to a room outside the layout"
        if i not in final or (j is not None and j not in final):
            continue
        if j is None:
            ring = final[i].exterior
            seg = _near_straight(ring, mid, w) if ring.distance(Point(mid)) <= max(0.6, exit_snap if why_out != "free-space contact" else 0) else None
            if seg is not None and ring.distance(Point(mid)) > 0.6:
                why_out = "door to a room outside the layout, snapped %.1f m to the layout's wall" % ring.distance(Point(mid))
        else:
            sh = final[i].exterior.intersection(final[j].exterior)
            lines = [g for g in getattr(sh, "geoms", [sh]) if g.geom_type == "LineString"]
            seg = None
            if lines:
                ln = min(lines, key=lambda g: g.distance(Point(mid)))
                seg = _near_straight(ln, mid, w)
        if seg is None:
            continue
        openings.append(dict(kind="door", x0=round(seg[0][0], 3), z0=round(seg[0][1], 3), x1=round(seg[1][0], 3), z1=round(seg[1][1], 3),
                             _rooms=(i, j), _src=why_out if why_out != "free-space contact" or j is not None else op["source"]))
    # entrance arrows and entrance words (orientation plans): a door on the ground-floor wall nearest each tip that meets the outline
    ent = [(a_, "entrance arrow symbol") for a_ in lvl["extra"].get("entrance_arrows", [])] +           [(a_, "entrance word on the plan") for a_ in lvl["extra"].get("entrance_labels", [])]
    for ar, ent_src in ent:
        tip = Point(local(ar["tip"]))
        best = None
        for i, g in final.items():
            d = g.exterior.distance(tip)
            if d < 4 and (best is None or d < best[0]):
                best = (d, i, g)
        if best:
            seg = _near_straight(best[2].exterior, (tip.x, tip.y), 1.1, outer=foot)
            if seg:
                openings.append(dict(kind="door", x0=round(seg[0][0], 3), z0=round(seg[0][1], 3), x1=round(seg[1][0], 3),
                                     z1=round(seg[1][1], 3), _rooms=(best[1], None), _src=ent_src))
    # drop rooms that break a rule or cannot be reached, until the layout validates
    order = list(final)
    for _ in range(40):
        rooms = [list(final[i].exterior.coords)[:-1] for i in order]
        ops = [dict(o_) for o_ in openings if all(r is None or r in order for r in o_["_rooms"])]
        err = validate(rooms, ops, rings)
        if not err:
            break
        bad = set()
        for e in err:
            m = re.match(r"room (\d+)", e)
            if m:
                k = int(m.group(1))
                bad.add(k)
                if "overlaps" in e:
                    pass
        if not bad:
            rep["notes"].append("not resolved: " + "; ".join(err[:3]))
            break
        for k in sorted(bad, reverse=True):
            rep["left_out"].append(dict(room=order[k], why=err[[x.startswith("room %d" % k) for x in err].index(True)]))
            order.pop(k)
    rooms = [list(final[i].exterior.coords)[:-1] for i in order]
    ops = [dict(o_) for o_ in openings if all(r is None or r in order for r in o_["_rooms"])]
    err = validate(rooms, ops, rings)
    rep["python_validate"] = err
    rep["rooms_kept"], rep["rooms_in_cue"] = len(order), len(lvl["rooms"])
    rep["doors"] = len(ops)
    out_rooms = []
    types = {}
    for i in order:
        t, why = _type_for(lvl["rooms"][i], final[i])
        types[why] = types.get(why, 0) + 1
        pts = [[round(x, 3), round(z, 3)] for x, z in list(final[i].exterior.coords)[:-1]]
        out_rooms.append(dict(points=pts, type=t))
    rep["room_type_sources"] = types
    layout = dict(version=2, egid=int(p["egid"]), e=round(o["e"], 3), n=round(o["n"], 3), yawDeg=o["yaw_deg"], storeyHeight=2.7,
                  floors=[dict(level=0, rooms=out_rooms,
                               openings=[{k: v for k, v in o_.items() if not k.startswith("_")} for o_ in ops], stairs=[])])
    rep["footprint_area_m2"] = round(foot.area, 1)
    rep["rooms_area_m2"] = round(sum(final[i].area for i in order), 1)
    rep["cue_rooms_area_m2"] = round(sum(r.get("area_m2", 0.0) for r in lvl["rooms"]), 1)
    why = keep_guard(len(order), len(lvl["rooms"]), rep["rooms_area_m2"], rep["cue_rooms_area_m2"])
    if why:
        rep["refused"] = why
        return None, rep
    rep["frame"] = dict(e=o["e"], n=o["n"], yaw_deg=o["yaw_deg"], length=o["length"], width=o["width"])
    return layout, rep


def _near_straight(line, near, width, outer=None):
    """A segment of `width` on one straight edge of the line, centred as near `near` as the edge allows."""
    pts = list(line.coords)
    best = None
    for k in range(len(pts) - 1):
        a, b = pts[k], pts[k + 1]
        ln = math.dist(a, b)
        if ln < 0.5:
            continue
        d = _segdist(near, a, b)
        if best is None or d < best[0]:
            best = (d, a, b, ln)
    if best is None:
        return None
    _, a, b, ln = best
    ux, uz = (b[0] - a[0]) / ln, (b[1] - a[1]) / ln
    t = (near[0] - a[0]) * ux + (near[1] - a[1]) * uz
    w = min(width, ln)
    t0 = max(0.0, min(t - w / 2, ln - w))
    return (a[0] + ux * t0, a[1] + uz * t0), (a[0] + ux * (t0 + w), a[1] + uz * (t0 + w)), w


def write_layouts(path, layouts):
    with open(path, "w", encoding="utf-8") as f:
        json.dump(dict(layouts=layouts), f, indent=1)


# ------------------------------------------------------------------------------------------------ stadium row
SIDE_NAMES = ["North", "East", "South", "West"]


def stadium_row(c, info, catalog_path, section=None):
    """(csharp text, diff text, dict of measured fields) for a StadiumSpec row proposal from a placed cue."""
    lvl = c["levels"][0]
    ex_ = lvl["extra"]
    p = c["placement"]
    if not p or "seating_front" not in ex_:
        raise ValueError("cue has no placement or no seating front")
    (sx0, sz0), (sx1, sz1) = ex_["seating_front"]
    (rx0, rz0), (rx1, rz1) = ex_["roof_outer"]
    (px0, pz0), (px1, pz1) = ex_["pitch_rect"]
    horiz = ex_.get("pitch_horizontal", True)
    pcx, pcz = (px0 + px1) / 2, (pz0 + pz1) / 2
    cE, cN = place.to_lv95(pcx, pcz, p["e"], p["n"], p["yaw_deg"])
    # the page's right-hand direction as a grid bearing; the long axis (v) runs along it when the pitch is drawn horizontal
    beta = (90 - p["yaw_deg"]) % 360
    v_bearing = beta if horiz else (beta + 90) % 360
    phi = ((v_bearing + 90) % 180) - 90  # GridAzimuthDeg: long axis, clockwise from grid north, in (-90, 90]
    half_along = ((sx1 - sx0) if horiz else (sz1 - sz0)) / 2
    half_across = ((sz1 - sz0) if horiz else (sx1 - sx0)) / 2
    # outward bearing of each plan side, then the nearest of phi + 0/90/180/270 names it
    sides = dict(left=(beta + 180) % 360, right=beta, top=(beta + 270) % 360, bottom=(beta + 90) % 360)
    depth = dict(left=sx0 - rx0, right=rx1 - sx1, top=sz0 - rz0, bottom=rz1 - sz1)
    named = {}
    for k, b in sides.items():
        best = min(range(4), key=lambda q: abs((b - (phi + 90 * q) + 180) % 360 - 180))
        named[SIDE_NAMES[best]] = round(depth[k], 1)
    f = dict(E=round(cE, 1), N=round(cN, 1), GridAzimuthDeg=round(phi, 1), PitchWidth=round(c_fact(c, "pitch_width_m", 68), 1),
             PitchLength=round(c_fact(c, "pitch_length_m", 105), 1), FrontHalfU=round(half_across, 1), FrontHalfV=round(half_along, 1),
             DepthNorth=named["North"], DepthEast=named["East"], DepthSouth=named["South"], DepthWest=named["West"])
    f["PitchWidth"], f["PitchLength"] = min(f["PitchWidth"], f["PitchLength"]), max(f["PitchWidth"], f["PitchLength"])
    extra = []
    if section:
        for k, v in section.items():
            f[k] = v
    text = ["new()", "{",
            '    Name = "%s", Town = "%s",' % (info.get("name", ""), info.get("town", "")),
            "    E = %s, N = %s, GridAzimuthDeg = %sf," % (f["E"], f["N"], f["GridAzimuthDeg"]),
            "    PitchWidth = %sf, PitchLength = %sf," % (f["PitchWidth"], f["PitchLength"]),
            "    FrontHalfU = %sf, FrontHalfV = %sf, FrontCorner = 4f,  // corner radius not measurable on the plan: current value kept" % (f["FrontHalfU"], f["FrontHalfV"]),
            "    DepthNorth = %sf, DepthEast = %sf, DepthSouth = %sf, DepthWest = %sf," % (f["DepthNorth"], f["DepthEast"], f["DepthSouth"], f["DepthWest"])]
    if section:
        text.append("    RoofOuter = %sf, RoofInner = %sf,  // from the long section, scaled by the plan's roof length" % (section.get("RoofOuter"), section.get("RoofInner")))
    text.append("}")
    new_text = "\n".join(text)
    diff = ""
    cur = _catalog_row(catalog_path, info.get("name", ""))
    if cur:
        diff = "".join(difflib.unified_diff(cur.splitlines(True), [l + "\n" for l in _fields_view(new_text, cur)], "StadiumCatalog.cs (current)", "plancues (proposed)"))
    return new_text, diff, f


def c_fact(c, key, default):
    for x in c["facts"]:
        if x["key"] == key:
            return x["value"]
    return default


def _catalog_row(path, name):
    try:
        src = open(path, encoding="utf-8").read()
    except OSError:
        return None
    i = src.find('Name = "%s"' % name)
    if i < 0:
        return None
    a = src.rfind("new()", 0, i)
    depth, j = 0, src.find("{", a)
    k = j
    while k < len(src):
        if src[k] == "{":
            depth += 1
        elif src[k] == "}":
            depth -= 1
            if depth == 0:
                break
        k += 1
    return src[a:k + 1]


def _fields_view(new_text, cur):
    """The proposed row laid out line for line like the current one where the fields exist (for a readable diff)."""
    vals = {}
    for m in re.finditer(r"(\w+) = (-?[\d.]+)f?", new_text):
        vals[m.group(1)] = m.group(2)
    out = []
    for line in cur.splitlines():
        def sub(m):
            k = m.group(1)
            if k in vals and k not in ("Rows",):
                suffix = "f" if m.group(0).endswith("f") else ""
                return "%s = %s%s" % (k, vals[k], suffix)
            return m.group(0)
        out.append(re.sub(r"\b(E|N|GridAzimuthDeg|PitchWidth|PitchLength|FrontHalfU|FrontHalfV|DepthNorth|DepthEast|DepthSouth|DepthWest|RoofOuter|RoofInner) = (-?[\d.]+)f?", sub, line))
    return out

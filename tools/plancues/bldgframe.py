"""The frame a house layout is drawn in: Python twin of tools/TerrainFormat/BuildingOutline.cs.

Rings: the boundary of the solid's floor triangles (faces lying flat within 0.3 m of its lowest height), chained;
for a building stepped down a slope, the union of those and the faces bottoming its higher parts (#1574); a solid
with no clean floor falls back to the convex hull of every vertex. Frame: x along the long side of the
minimum-area rectangle of the hull, z across, metres from its centre; LV95 (e, n) = centre + x * (cos a, sin a)
+ z * (sin a, -cos a), a = yaw counter-clockwise from east in [0, 180).
"""
import math

import numpy as np


def _q(v):
    return int(round(float(v) * 100))


def floor_rings(b):
    """Rings of the floor boundary as lists of tile-local (x, z) metres (even-odd over all rings)."""
    t = b["tris"]
    edges = {}
    floor, raised, low_area, all_area = [], False, 0.0, 0.0
    for i, tri in enumerate(t):
        (ax, ay, az), (bx, by, bz), (cx, cy, cz) = tri
        area = (bx - ax) * (cz - az) - (bz - az) * (cx - ax)
        if abs(area) < 0.01:
            continue
        lowest = max(ay, by, cy) <= b["min_y"] + 0.3
        if not lowest and not (area < 0 and _raised_floor(t, i)):
            continue  # a roof or a soffit, not a floor
        floor.append(((float(ax), float(az)), (float(bx), float(bz)), (float(cx), float(cz))))
        all_area += abs(area) / 2
        if not lowest:
            raised = True
            continue
        low_area += abs(area) / 2
        for p, q in (((ax, az), (bx, bz)), ((bx, bz), (cx, cz)), ((cx, cz), (ax, az))):
            a, c = (_q(p[0]), _q(p[1])), (_q(q[0]), _q(q[1]))
            if a == c:
                continue
            key = (a, c) if a < c else (c, a)
            edges[key] = edges.get(key, 0) + 1
    nxt = {}
    for (a, c), n in edges.items():
        if n == 1:
            nxt.setdefault(a, []).append(c)
            nxt.setdefault(c, []).append(a)
    rings = []
    while nxt:
        start = next(iter(nxt))
        ring = [start]
        prev, cur = start, _take(nxt, start)
        while cur is not None and cur != start:
            ring.append(cur)
            _drop(nxt, cur, prev)
            prev, cur = cur, _take(nxt, cur)
        if cur == start:
            _drop(nxt, start, prev)
        if len(ring) >= 3 and cur == start:
            rings.append([(p[0] / 100.0, p[1] / 100.0) for p in ring])
    if raised:
        # floors at several heights: their union, if its area lies between the lowest floor's and all of them
        union = _union_rings(floor)
        net = abs(sum(_area(r) for r in union))
        if low_area * 0.99 <= net <= all_area * 1.01:
            rings = union
    return rings


def _height_at(t, j, x, z):
    (ax, ay, az), (bx, by, bz), (cx, cy, cz) = t[j]
    d = (bx - ax) * (cz - az) - (bz - az) * (cx - ax)
    if abs(d) < 0.01:
        return None
    l1 = ((bx - x) * (cz - z) - (bz - z) * (cx - x)) / d
    l2 = ((cx - x) * (az - z) - (cz - z) * (ax - x)) / d
    l3 = 1 - l1 - l2
    if l1 < 0 or l2 < 0 or l3 < 0:
        return None
    return l1 * ay + l2 * by + l3 * cy


def _raised_floor(t, i):
    """A face looking down is the floor of a higher part: nothing of the solid under its centre, 2 m or more over it."""
    g = t[i].mean(axis=0)
    gx, gy, gz = float(g[0]), float(g[1]), float(g[2])
    over = math.inf
    for j in range(len(t)):
        if j == i:
            continue
        h = _height_at(t, j, gx, gz)
        if h is None:
            continue
        if h < gy - 0.05:
            return False
        if h > gy + 0.05:
            over = min(over, h - gy)
    return 2 <= over < math.inf


def _d2(a, b):
    return (a[0] - b[0]) ** 2 + (a[1] - b[1]) ** 2


def _covered(tris, x, z):
    for p in tris:
        d0 = (p[1][0] - p[0][0]) * (z - p[0][1]) - (p[1][1] - p[0][1]) * (x - p[0][0])
        d1 = (p[2][0] - p[1][0]) * (z - p[1][1]) - (p[2][1] - p[1][1]) * (x - p[1][0])
        d2 = (p[0][0] - p[2][0]) * (z - p[2][1]) - (p[0][1] - p[2][1]) * (x - p[2][0])
        if (d0 >= 0 and d1 >= 0 and d2 >= 0) or (d0 <= 0 and d1 <= 0 and d2 <= 0):
            return True
    return False


def _union_rings(tris):
    """Rings of the union of plan triangles: edges cut where others meet them, pieces kept with the floor on their
    left, joined end to the nearest start (BuildingOutline.UnionRings)."""
    corners = []

    def weld(p):
        for c in corners:
            if _d2(c, p) < 0.0025:
                return c
        corners.append(p)
        return p
    tris = [(weld(p[0]), weld(p[1]), weld(p[2])) for p in tris]
    tris = [p for p in tris if abs((p[1][0] - p[0][0]) * (p[2][1] - p[0][1]) - (p[1][1] - p[0][1]) * (p[2][0] - p[0][0])) >= 0.01]
    segs = [s for p in tris for s in ((p[0], p[1]), (p[1], p[2]), (p[2], p[0]))]
    pieces, seen = [], set()
    for a, c in segs:
        rx, rz = c[0] - a[0], c[1] - a[1]
        ln = math.hypot(rx, rz)
        if ln < 0.01:
            continue
        cuts = [0.0, 1.0]
        for p, q in segs:
            sx, sz = q[0] - p[0], q[1] - p[1]
            sl = math.hypot(sx, sz)
            den = rx * sz - rz * sx
            wx, wz = p[0] - a[0], p[1] - a[1]
            if sl < 0.01:
                continue
            if abs(wx * rz - wz * rx) <= 0.05 * ln:
                cuts.append((wx * rx + wz * rz) / (ln * ln))
            if abs(den) < 1e-3 * ln * sl:
                if abs(wx * rz - wz * rx) > 0.01 * ln:
                    continue
                cuts.append((wx * rx + wz * rz) / (ln * ln))
                cuts.append(((q[0] - a[0]) * rx + (q[1] - a[1]) * rz) / (ln * ln))
                continue
            s = (wx * sz - wz * sx) / den
            u = (wx * rz - wz * rx) / den
            if -0.01 / sl <= u <= 1 + 0.01 / sl:
                cuts.append(s)
        at = sorted(s for s in cuts if 0 <= s <= 1)
        nx, nz = -rz / ln * 0.02, rx / ln * 0.02
        for k in range(len(at) - 1):
            if (at[k + 1] - at[k]) * ln < 0.005:
                continue
            m = (at[k] + at[k + 1]) / 2
            mx, mz = a[0] + rx * m, a[1] + rz * m
            left, right = _covered(tris, mx + nx, mz + nz), _covered(tris, mx - nx, mz - nz)
            if left == right:
                continue
            p0 = (a[0] + rx * at[k], a[1] + rz * at[k])
            p1 = (a[0] + rx * at[k + 1], a[1] + rz * at[k + 1])
            pc = (p0, p1) if left else (p1, p0)
            key = (round(pc[0][0], 2), round(pc[0][1], 2), round(pc[1][0], 2), round(pc[1][1], 2))
            if key not in seen:
                seen.add(key)
                pieces.append(pc)
    rings, used = [], [False] * len(pieces)
    for first in range(len(pieces)):
        if used[first]:
            continue
        used[first] = True
        ring, end = [pieces[first][0]], pieces[first][1]
        while True:
            best, best_d = -1, _d2(end, pieces[first][0])
            for j in range(len(pieces)):
                if not used[j] and _d2(end, pieces[j][0]) < best_d:
                    best, best_d = j, _d2(end, pieces[j][0])
            if best_d > 1:
                ring = []
                break
            if best < 0:
                break
            used[best] = True
            ring.append(pieces[best][0])
            end = pieces[best][1]
        r = [(round(p[0], 2), round(p[1], 2)) for p in ring]
        if len(r) >= 3 and abs(_area(r)) >= 1:
            rings.append(r)
    return rings


def _take(nxt, at):
    lst = nxt.get(at)
    if not lst:
        nxt.pop(at, None)
        return None
    n = lst.pop()
    if not lst:
        del nxt[at]
    return n


def _drop(nxt, at, other):
    lst = nxt.get(at)
    if lst is None:
        return
    if other in lst:
        lst.remove(other)
    if not lst:
        del nxt[at]


def _area(r):
    return sum(r[j - 1][0] * r[j][1] - r[j][0] * r[j - 1][1] for j in range(len(r))) / 2


def hull(points):
    pts = sorted(set(points))
    if len(pts) < 3:
        return pts

    def cross(o, a, b):
        return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])
    h = []
    for _ in range(2):
        floor = len(h)
        for q in pts:
            while len(h) >= floor + 2 and cross(h[-2], h[-1], q) <= 0:
                h.pop()
            h.append(q)
        h.pop()
        pts.reverse()
    return h


def min_rect(hull_pts):
    """(centre e, centre n, yaw_deg in [0,180), length, width) of the minimum-area rectangle of an LV95 hull."""
    best = (1e18, None)
    h = hull_pts
    for i in range(len(h)):
        a, c = h[i], h[(i + 1) % len(h)]
        ang = math.atan2(c[1] - a[1], c[0] - a[0])
        co, si = math.cos(ang), math.sin(ang)
        us = [p[0] * co + p[1] * si for p in h]
        vs = [-p[0] * si + p[1] * co for p in h]
        u0, u1, v0, v1 = min(us), max(us), min(vs), max(vs)
        area = (u1 - u0) * (v1 - v0)
        if area >= best[0] - 1e-6:
            continue
        um, vm = (u0 + u1) / 2, (v0 + v1) / 2
        e, n = um * co - vm * si, um * si + vm * co
        if u1 - u0 >= v1 - v0:
            best = (area, (e, n, ang, u1 - u0, v1 - v0))
        else:
            best = (area, (e, n, ang + math.pi / 2, v1 - v0, u1 - u0))
    e, n, ang, ln, wd = best[1]
    deg = math.degrees(ang) % 180
    return e, n, round(deg, 2), ln, wd


def outline(b):
    """Dict(rings_lv95, e, n, yaw_deg, length, width) like BuildingOutline.Compute for a tile building dict."""
    tile = b["tile"]
    e0, n1 = tile[0] * 1000.0, (tile[1] + 1) * 1000.0
    rings = floor_rings(b)
    if not rings or sum(abs(_area(r)) for r in rings) < 1:
        pts = [(float(p[0]), float(p[2])) for tri in b["tris"] for p in tri]
        rings = [hull(pts)]
    allp = [p for r in rings for p in r]
    hl = hull([(e0 + x, n1 - z) for x, z in allp])
    e, n, yaw, ln, wd = min_rect(hl)
    lv = [[(e0 + x, n1 - z) for x, z in r] for r in rings]
    return dict(rings=lv, e=e, n=n, yaw_deg=yaw, length=ln, width=wd)


def to_lv95(o, x, z):
    a = math.radians(o["yaw_deg"])
    c, s = math.cos(a), math.sin(a)
    return o["e"] + x * c + z * s, o["n"] + x * s - z * c


def to_local(o, e, n):
    a = math.radians(o["yaw_deg"])
    c, s = math.cos(a), math.sin(a)
    de, dn = e - o["e"], n - o["n"]
    return de * c + dn * s, de * s - dn * c


def local_rings(o):
    return [[to_local(o, e, n) for e, n in r] for r in o["rings"]]

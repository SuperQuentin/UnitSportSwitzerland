"""The frame a house layout is drawn in: Python twin of tools/TerrainFormat/BuildingOutline.cs.

Rings: the boundary of the solid's floor triangles (faces lying flat within 0.3 m of its lowest height), chained;
a solid with no clean floor falls back to the convex hull of every vertex. Frame: x along the long side of the
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
    for tri in t:
        (ax, ay, az), (bx, by, bz), (cx, cy, cz) = tri
        area = (bx - ax) * (cz - az) - (bz - az) * (cx - ax)
        if abs(area) < 0.01:
            continue
        if max(ay, by, cy) > b["min_y"] + 0.3:
            continue
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

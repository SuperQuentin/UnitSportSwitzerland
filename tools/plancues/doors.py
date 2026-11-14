"""Infer the doors a plan does not show (#1572). Every door made here is marked `inferred`, with its rule and a confidence.

Rules (docs/notes/tools/plancues.md): real doors, entrance arrows and entrance words are never touched. A room that no door reaches gets one
door, the fewest that reach everyone: on its longest wall shared with a circulation room (>= 1.2 m), else on its longest shared wall
with a room that is already reachable, so reachability spreads out from the front door. Without a front door one is put on an outer wall of
a circulation room, nearest the building's GWR entrance point."""
import math
import re

from shapely.geometry import LineString, Point
from shapely.ops import linemerge

DOOR, WIDE, MIN_WALL, MIN_ANY, CORNER, FRONT_MIN, HALL_M2 = 0.9, 1.1, 1.2, 0.8, 0.3, 1.5, 120.0
# DE/FR/IT/EN names of circulation space; "hall" alone is a lecture hall or a sports hall, so it counts only at the start of a label
CIRC = re.compile(r"\b(gang|g[aä]nge|korridor|flur|foyer|erschliessung|treppenhaus|eingangshalle|couloir|d[ée]gagement|vestibule|vestibolo|corridoio|"
                  r"corridor|lobby|atrio|entrance hall|main hall|circulation|staircase|stairwell|escalier|cage d)|^(halle|hall|treppe|stairs)\b", re.I)


def is_named_circulation(label):
    return bool(label) and bool(CIRC.search(label.strip()))


def _pieces(line):
    """Straight pieces (a, b) of any line geometry, one per segment."""
    for g in getattr(line, "geoms", [line]):
        if g.geom_type == "LineString":
            c = list(g.coords)
            for k in range(len(c) - 1):
                yield c[k], c[k + 1]
        elif hasattr(g, "geoms"):
            yield from _pieces(g)


def _runs(line):
    """Pieces joined while they stay on one straight line (within 2 degrees and 3 cm)."""
    out = []
    for g in getattr(linemerge(line) if line.geom_type == "MultiLineString" else line, "geoms", [line]):
        if g.geom_type != "LineString":
            continue
        c = list(g.coords)
        a = c[0]
        for k in range(1, len(c)):
            b = c[k]
            if k + 1 < len(c):
                n = c[k + 1]
                d1 = math.atan2(b[1] - a[1], b[0] - a[0])
                d2 = math.atan2(n[1] - b[1], n[0] - b[0])
                off = abs(-(n[0] - a[0]) * math.sin(d1) + (n[1] - a[1]) * math.cos(d1))
                if abs((d2 - d1 + math.pi) % (2 * math.pi) - math.pi) < math.radians(2) and off < 0.03:
                    continue
            out.append((a, b))
            a = b
    return out


def shared_walls(polys):
    """{(i, j): [(length, a, b), ...]} longest first: straight runs and single pieces of the walls two rooms share (i < j)."""
    ids = sorted(polys)
    walls = {}
    for x, i in enumerate(ids):
        for j in ids[x + 1:]:
            if polys[i].distance(polys[j]) > 0.05:
                continue
            sh = polys[j].exterior.intersection(polys[i].exterior.buffer(0.03))
            if sh.is_empty:
                continue
            cand = _runs(sh) + list(_pieces(sh))
            cand = sorted(((math.dist(a, b), a, b) for a, b in cand), key=lambda t: -t[0])
            if cand and cand[0][0] >= 0.3:
                walls[(i, j)] = cand
    return walls


def _on_wall(poly, a, b, tol=0.03):
    pts = list(poly.exterior.coords)[:-1]
    for k in range(len(pts)):
        p, q = pts[k], pts[(k + 1) % len(pts)]
        ln = math.dist(p, q)
        if ln < 1e-6:
            continue
        ux, uz = (q[0] - p[0]) / ln, (q[1] - p[1]) / ln
        if all(abs(-(v[0] - p[0]) * uz + (v[1] - p[1]) * ux) <= tol and -tol <= (v[0] - p[0]) * ux + (v[1] - p[1]) * uz <= ln + tol for v in (a, b)):
            return True
    return False


def _door_on(run, width, near=None):
    """(p0, p1, width) of a door on the run: centred (or as near `near` as the run allows), at least 0.15 m (0.3 m when the run is long
    enough) from the corners. None when the run cannot hold a 0.5 m door."""
    a, b = run
    ln = math.dist(a, b)
    w = min(width, ln - 2 * 0.15)
    if w < 0.5:
        return None
    ux, uz = (b[0] - a[0]) / ln, (b[1] - a[1]) / ln
    clear = min(CORNER, (ln - w) / 2)
    t = ln / 2 if near is None else (near[0] - a[0]) * ux + (near[1] - a[1]) * uz
    t0 = max(clear, min(t - w / 2, ln - clear - w))
    return (a[0] + ux * t0, a[1] + uz * t0), (a[0] + ux * (t0 + w), a[1] + uz * (t0 + w)), w


def _opening(p, rooms, src, conf, rule):
    return dict(kind="door", x0=round(p[0][0], 3), z0=round(p[0][1], 3), x1=round(p[1][0], 3), z1=round(p[1][1], 3),
                _rooms=rooms, _src="inferred: " + src, _conf=conf, _rule=rule)


def circulation(polys, names, walls, force=()):
    """Ids of the rooms taken as circulation space, with why: named so, or (unnamed) long and thin with 3 neighbours or more, or the largest
    room with 5 neighbours or more."""
    deg = {i: 0 for i in polys}
    for (i, j), c in walls.items():
        if c[0][0] >= MIN_ANY:
            deg[i] += 1
            deg[j] += 1
    why = {i: "part of a space cut in two" for i in force if i in polys}
    for i, p in polys.items():
        if i in why:
            continue
        if is_named_circulation(names.get(i)):
            why[i] = "named"
            continue
        r = list(p.minimum_rotated_rectangle.exterior.coords)
        s1, s2 = math.dist(r[0], r[1]), math.dist(r[1], r[2])
        if min(s1, s2) < 3.5 and max(s1, s2) / max(min(s1, s2), 0.1) > 3.5 and deg[i] >= 3:
            why[i] = "long and thin, %d neighbours" % deg[i]
    big = [i for i in polys if deg[i] >= 5 and polys[i].area >= 60 and i not in why]
    if big:
        i = max(big, key=lambda k: polys[k].area)
        why[i] = "largest room, %d neighbours" % deg[i]
    return why


def front_door(polys, circ, walls, foot, target):
    """An inferred front door on an outer wall of a circulation room (any room when there is none), nearest `target` (x, z, why) if given
    and else on the longest outer wall. None when no room has an outer wall of 1.5 m."""
    pool = [i for i in polys if i in circ] or list(polys)
    best = None
    for i in pool:
        pts = list(polys[i].exterior.coords)
        for k in range(len(pts) - 1):
            a, b = pts[k], pts[k + 1]
            ln = math.dist(a, b)
            m = Point((a[0] + b[0]) / 2, (a[1] + b[1]) / 2)
            if ln < FRONT_MIN or not aligned(a, b) or foot.exterior.distance(m) > 0.5 or any(j != i and polys[j].distance(m) < 0.05 for j in polys):
                continue
            d = LineString([a, b]).distance(Point(target[:2])) if target else -ln
            if best is None or d < best[0]:
                best = (d, i, (a, b))
    if best is None:
        return None
    _, i, run = best
    p = _door_on(run, WIDE, near=target[:2] if target else None)
    if p is None:
        return None
    why = ("outer wall of a circulation room nearest the GWR entrance point (%s)" % target[2]) if target else "longest outer wall of a circulation room"
    return _opening(p, (i, None), why, 0.5 if target else 0.3, "front door")


def aligned(a, b, tol=0.005):
    """Whether the segment runs exactly along an axis of the layout (its ends within 5 mm): the game puts a door only on a wall square to its
    strips, and a wall a degree off becomes steps that a door does not land on."""
    return abs(b[0] - a[0]) < tol or abs(b[1] - a[1]) < tol


def _place(polys, walls, a, b, width):
    """A door of `width` on the longest wall (run or single piece) of a and b that both rooms really have as an edge, a wall along an axis
    before a diagonal one; None when there is none."""
    for ln, p0, p1 in sorted(walls[tuple(sorted((a, b)))], key=lambda t: (not aligned(t[1], t[2]), -t[0])):
        door = _door_on((p0, p1), width)
        if door and _on_wall(polys[a], *door[:2], tol=0.002) and _on_wall(polys[b], *door[:2], tol=0.002):  # exactly on both: pieces must meet within 0.1 mm
            return door
    return None


def infer(polys, names, real, foot, target=None, walls=None, groups=None, force_circ=()):
    """(doors, info): the inferred openings (dicts with x0..z1 and _rooms, _src, _conf, _rule) that make every room reachable, and a report.
    polys {id: Polygon}; names {id: label or None}; real: openings already there (dicts with _rooms); foot: footprint polygon;
    target: (x, z, why) the entrance point or None."""
    walls = shared_walls(polys) if walls is None else {k: v for k, v in walls.items() if k[0] in polys and k[1] in polys}
    circ = circulation(polys, names, walls, force_circ)
    groups = groups or {}
    adj = {i: set() for i in polys}
    start = set()
    for o in real:
        i, j = o["_rooms"]
        if j is None:
            start.add(i)
        else:
            adj[i].add(j)
            adj[j].add(i)
    nb = {i: set() for i in polys}
    for i, j in walls:
        nb[i].add(j)
        nb[j].add(i)
    blocked = set()
    out = []
    if not start:
        fd = front_door(polys, circ, walls, foot, target)
        if fd:
            out.append(fd)
            start.add(fd["_rooms"][0])

    for (i, j) in sorted(walls):  # the parts of one cut room are one space: a door where they meet
        if groups.get(i, i) == groups.get(j, j) and i != j:
            door = _place(polys, walls, i, j, WIDE)
            if door:
                out.append(_opening(door, (i, j), "the parts of one surrounding space (the format has no room with a hole)", 0.3, "split"))
                adj[i].add(j)
                adj[j].add(i)

    def closure(seed):
        seen, stack = set(seed), list(seed)
        while stack:
            for n in adj[stack.pop()]:
                if n not in seen:
                    seen.add(n)
                    stack.append(n)
        return seen

    while True:
        reached = closure(start)
        todo = set(polys) - reached
        if not todo:
            break
        comps, left = [], set(todo)
        while left:
            c = closure({next(iter(left))}) & todo
            comps.append(c)
            left -= c
        best = None
        for c in comps:
            for a in c:
                for b in nb[a]:
                    if b not in reached or (a, b) in blocked:
                        continue
                    ln, p0, p1 = walls[tuple(sorted((a, b)))][0]
                    if ln < MIN_ANY:
                        continue
                    key = (any(i in circ for i in c), b in circ and ln >= MIN_WALL, ln)
                    if best is None or key > best[0]:
                        best = (key, a, b)
        if best is None:
            break
        _, a, b = best
        wide = (a in circ and b in circ) or max(polys[a].area, polys[b].area) > HALL_M2
        door = _place(polys, walls, a, b, WIDE if wide else DOOR)
        if door is None:
            blocked.add((a, b))  # no piece of this wall can hold a door: try another pair
            continue
        if b in circ:
            rule, conf = "longest wall shared with circulation space (%s)" % circ[b], 0.5
        else:
            rule, conf = "longest wall shared with a reachable room", 0.35
        out.append(_opening(door, (a, b), rule, conf, "room door"))
        adj[a].add(b)
        adj[b].add(a)
    return out, dict(circulation={str(i): w for i, w in circ.items()}, rooms=len(polys), unreachable_after=sorted(set(polys) - closure(start)))


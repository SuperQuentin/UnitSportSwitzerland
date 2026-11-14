"""Walls, outline, rooms, doors and labels of a plan.

Vector pages: the drawing's own stroke weights and fills say what is a cut wall (the heaviest dark weights and
dark or thin filled shapes); they are rasterised at RES px per metre, and rooms are the basins of a watershed on the
distance transform of the free space, so a door (a narrow neck between two rooms) splits them. Everything is in the
plan frame: metres, x right, z down on the page, origin at the outline's box centre (`origin_pt`, in PDF points).
Raster pages (scans): thresholded and cleaned with OpenCV, lower confidence (see raster.py).
"""
import math
import re

import cv2
import numpy as np
from shapely.geometry import LineString, MultiPolygon, Polygon
from shapely.ops import unary_union

RES = 10.0  # raster pixels per metre

DARK = 0.35
TYPE_WORDS = [
    ("WC", r"\bwc\b|toilet|toilette|abort|aborts"),
    ("Hall", r"foyer|eingang|entr[ée]e|hall\b|vestibul|empfang|accueil"),
    ("Corridor", r"korridor|gang\b|couloir|flur|corridoio|passage"),
    ("Kitchen", r"k[üu]che|cuisine|cucina|teek[üu]che|bistro|cafeteria|mensa"),
    ("Dining", r"restaurant|speise|lounge|bar\b"),
    ("Office", r"b[üu]ro|bureau|office|sekretariat|verwaltung|dekanat|secr[ée]tariat|ufficio"),
    ("Storage", r"lager|d[ée]p[ôo]t|archiv|abstell|magazin|technik|technique|cave"),
    ("Study", r"seminar|h[öo]rsaal|hoersaal|kurs|bibliothek|biblioth[èe]que|salle|aula|auditoire|lern|library|classroom|lecture|study|computer"),
    ("Cellar", r"keller|cellier"),
]


# ---------------------------------------------------------------- paths -------------------------------------------
def _bezier(p0, p1, p2, p3, n=6):
    t = np.linspace(0, 1, n + 1)[:, None]
    p0, p1, p2, p3 = (np.array(p, float) for p in (p0, p1, p2, p3))
    return ((1 - t) ** 3 * p0 + 3 * (1 - t) ** 2 * t * p1 + 3 * (1 - t) * t ** 2 * p2 + t ** 3 * p3).tolist()


def polylines_of(d):
    """The sub-paths of a drawing as lists of (x, y) points in page points; (points, closed)."""
    out, cur = [], []
    for it in d["items"]:
        k = it[0]
        if k == "l":
            a, b = tuple(it[1]), tuple(it[2])
            if cur and math.dist(cur[-1], a) < 0.01:
                cur.append(b)
            else:
                if len(cur) > 1:
                    out.append((cur, False))
                cur = [a, b]
        elif k == "c":
            pts = _bezier(it[1], it[2], it[3], it[4])
            if cur and math.dist(cur[-1], pts[0]) < 0.01:
                cur.extend(pts[1:])
            else:
                if len(cur) > 1:
                    out.append((cur, False))
                cur = pts
        elif k == "re":
            r = it[1]
            if len(cur) > 1:
                out.append((cur, False))
            cur = []
            out.append(([(r[0], r[1]), (r[2], r[1]), (r[2], r[3]), (r[0], r[3]), (r[0], r[1])], True))
        elif k == "qu":
            if len(cur) > 1:
                out.append((cur, False))
            cur = []
            q = [tuple(p) for p in it[1]]
            out.append(([q[0], q[1], q[3], q[2], q[0]], True))
    if len(cur) > 1:
        out.append((cur, math.dist(cur[0], cur[-1]) < 0.01 or bool(d.get("closePath"))))
    return out


def _lum(c):
    return sum(c) / len(c) if c is not None and len(c) else None


def stroke_classes(drawings):
    """Table of dark stroke weights: width -> number of paths, plus the weight classes that are cut walls.

    Walls are the heaviest dark weights that carry a real share of the page's paths (at least 1.5 %, and 20):
    every weight within 65 % of the heaviest such weight. Thinner weights are detail, hatching and furniture.
    """
    total = max(1, len(drawings))
    cnt = {}
    for d in drawings:
        if d.get("type") in ("s", "fs") and d.get("width") and (_lum(d.get("color")) is None or _lum(d["color"]) < DARK):
            w = round(float(d["width"]), 2)
            cnt[w] = cnt.get(w, 0) + 1
    sig = [w for w, n in cnt.items() if n >= max(20, 0.015 * total)]
    if not sig:
        return dict(table=cnt, wall_widths=[], heaviest=None)
    heavy = max(sig)
    walls = sorted(w for w in cnt if 0.65 * heavy <= w <= 2 * heavy)
    return dict(table=dict(sorted(cnt.items())), wall_widths=walls, heaviest=heavy)


def _door_swing(d, m_per_pt):
    """A short open arc (a few curve items, under 1.8 m across): the symbol of a swinging door."""
    items = d["items"]
    nc = sum(1 for i in items if i[0] == "c")
    if nc == 0 or len(items) > 5 or d.get("type") != "s":
        return False
    r = d["rect"]
    return 0.5 <= max(r.width, r.height) * m_per_pt <= 1.8 and not (len(items) >= 4 and nc == len(items))


def select_wall_paths(drawings, m_per_pt, classes, clip=None):
    """Wall strokes and wall solids from the drawings: (stroke polylines with widths, filled polygons)."""
    strokes, solids = [], []
    ww = set(classes["wall_widths"])
    for d in drawings:
        r = d["rect"]
        if clip and not (r.x1 > clip[0] and r.x0 < clip[2] and r.y1 > clip[1] and r.y0 < clip[3]):
            continue
        t = d.get("type")
        fill = d.get("fill")
        w = round(float(d["width"]), 2) if d.get("width") else 0
        heavy = t in ("s", "fs") and w in ww and (_lum(d.get("color")) is None or _lum(d["color"]) < DARK)
        dark_fill = t in ("f", "fs") and fill is not None and _lum(fill) is not None and _lum(fill) < DARK
        thin = min(r.width, r.height) * m_per_pt < 0.9
        if heavy and _door_swing(d, m_per_pt):
            continue  # a door's swing arc (and leaf) is not a wall: it must not close the doorway
        if heavy:
            for pts, closed in polylines_of(d):
                strokes.append((pts, max(w, 0.2)))
                if t == "fs" and closed and fill is not None and (_lum(fill) > 0.97 or _lum(fill) < DARK) and (
                        thin or _lum(fill) < DARK):
                    solids.append(pts)
        elif dark_fill and (thin or max(r.width, r.height) * m_per_pt < 3.5):
            for pts, closed in polylines_of(d):
                if len(pts) >= 3:
                    solids.append(pts)
    return strokes, solids


# ---------------------------------------------------------------- raster ------------------------------------------
class Frame:
    """Page points <-> mask pixels for a clip box."""

    def __init__(self, box, m_per_pt, res=RES):
        self.x0, self.y0, self.x1, self.y1 = box
        self.k = m_per_pt * res  # pixels per point
        self.m_per_pt = m_per_pt
        self.res = res
        self.w = int(math.ceil((self.x1 - self.x0) * self.k)) + 1
        self.h = int(math.ceil((self.y1 - self.y0) * self.k)) + 1

    def px(self, pts):
        a = np.asarray(pts, float)
        return np.stack([(a[:, 0] - self.x0) * self.k, (a[:, 1] - self.y0) * self.k], axis=1)

    def pt_of_px(self, x, y):
        return self.x0 + x / self.k, self.y0 + y / self.k


def rasterize(strokes, solids, fr, stroke_px=2):
    mask = np.zeros((fr.h, fr.w), np.uint8)
    S = 4
    for pts in solids:
        p = np.round(fr.px(pts) * (1 << S)).astype(np.int32)
        cv2.fillPoly(mask, [p], 255, lineType=cv2.LINE_8, shift=S)
    for pts, w in strokes:
        p = np.round(fr.px(pts) * (1 << S)).astype(np.int32)
        cv2.polylines(mask, [p], False, 255, thickness=max(stroke_px, int(round(w * fr.k))), lineType=cv2.LINE_8, shift=S)
    return mask


def clean_mask(mask, res=RES, min_extent_m=1.5, keep_share=0.04):
    """Drop speckle: pieces of wall that are small next to the main structure (furniture, leader lines, glyphs)."""
    n, lab, st, _ = cv2.connectedComponentsWithStats(mask, connectivity=8)
    if n <= 1:
        return mask
    biggest = st[1:, cv2.CC_STAT_AREA].max()
    keep = np.zeros(n, bool)
    lim = min_extent_m * res
    for i in range(1, n):
        if st[i, cv2.CC_STAT_AREA] >= keep_share * biggest and max(st[i, cv2.CC_STAT_WIDTH], st[i, cv2.CC_STAT_HEIGHT]) >= lim:
            keep[i] = True
    return np.where(keep[lab], 255, 0).astype(np.uint8)


def outline_mask(walls, close_m, res=RES):
    """Filled silhouette of the walls: closed over gaps up to close_m, holes filled, small bits dropped."""
    k = max(3, int(close_m * res) | 1)
    closed = cv2.morphologyEx(walls, cv2.MORPH_CLOSE, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (k, k)))
    cnts, _ = cv2.findContours(closed, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    filled = np.zeros_like(walls)
    if not cnts:
        return filled
    biggest = max(cv2.contourArea(c) for c in cnts)
    for c in cnts:
        if cv2.contourArea(c) >= 0.02 * biggest:
            cv2.drawContours(filled, [c], -1, 255, -1)
    return filled


def mask_polygons(mask, fr, eps_m=0.12, min_area_m2=1.0, mode=cv2.RETR_EXTERNAL):
    """Polygons (shapely, plan metres relative to the frame origin of fr: (x_pt - x0) * m_per_pt) of a binary mask."""
    cnts, hier = cv2.findContours(mask, mode, cv2.CHAIN_APPROX_NONE)
    out = []
    for c in cnts:
        if cv2.contourArea(c) / fr.res ** 2 < min_area_m2:
            continue
        a = cv2.approxPolyDP(c, eps_m * fr.res, True)[:, 0, :].astype(float) / fr.res
        if len(a) >= 3:
            p = Polygon(a)
            if not p.is_valid:
                p = p.buffer(0)
            if not p.is_empty:
                out.append(p)
    return out


# ---------------------------------------------------------------- rooms -------------------------------------------
def segment_rooms(walls, outline, fr, door_m=1.1, min_room_m2=1.5):
    """Room basins: (labels image, list of ids). Label 1 is the outside, rooms are 2.."""
    res = fr.res
    free = ((walls == 0) & (outline > 0)).astype(np.uint8)
    dist = cv2.distanceTransform(free, cv2.DIST_L2, 5)
    core = (dist > door_m / 2 * res).astype(np.uint8)
    n, lab = cv2.connectedComponents(core, connectivity=4)
    markers = np.zeros(walls.shape, np.int32)
    markers[lab > 0] = lab[lab > 0] + 1
    markers[outline == 0] = 1
    # small free pockets with no core (closets, narrow corridors) take the neighbour they touch in the flood
    elev = np.clip(255 - dist * (255.0 / max(1.0, dist.max())), 0, 255).astype(np.uint8)
    elev[walls > 0] = 255
    img = cv2.cvtColor(elev, cv2.COLOR_GRAY2BGR)
    cv2.watershed(img, markers)
    edge = markers < 0  # the lines between basins: give them to a neighbouring room so rooms touch where they meet
    markers[edge] = 0
    near = cv2.dilate(markers.astype(np.float32), np.ones((3, 3), np.uint8)).astype(np.int32)
    markers[edge] = np.maximum(near[edge], 1)
    return markers, n


def grow_into_walls(markers, walls, steps):
    """Labels reach the middle of the walls between rooms: free-space labels grown into wall pixels."""
    lab = markers.copy()
    lab[walls > 0] = 0
    k = np.ones((3, 3), np.uint8)
    for _ in range(steps):
        d = cv2.dilate(lab.astype(np.float32), k)
        take = (lab == 0) & (d > 0)
        if not take.any():
            break
        lab[take] = d[take].astype(np.int32)
    lab[lab == 0] = 1
    return lab


def contact_segments(markers, fr):
    """Free-space contacts between two rooms (door necks): list of (a, b, (x,y) px centre, length px)."""
    return []  # filled in by doors(): needs the free mask


def room_polygons(markers, fr, ids, eps_m=0.15, min_area=1.0):
    polys = {}
    for i in ids:
        m = (markers == i).astype(np.uint8) * 255
        m = cv2.morphologyEx(m, cv2.MORPH_OPEN, np.ones((3, 3), np.uint8))
        ps = mask_polygons(m, fr, eps_m=eps_m, min_area_m2=min_area)
        if ps:
            polys[i] = max(ps, key=lambda p: p.area)
    return polys


def resolve_overlaps(polys, grow=0.12):
    """Make polygons disjoint and touching: each is grown by `grow` m (closing the gap a wall leaves), then the
    earlier, larger one keeps the overlap, so neighbours share exactly one boundary."""
    if grow:
        polys = {i: p.buffer(grow, join_style=2, mitre_limit=2.0) for i, p in polys.items()}
    return _disjoint(polys)


def _disjoint(polys):
    """Make polygons disjoint (earlier, larger ones win) and simple, so shared walls are exactly collinear."""
    order = sorted(polys, key=lambda i: -polys[i].area)
    taken, out = None, {}
    for i in order:
        p = polys[i]
        if taken is not None:
            p = p.difference(taken)
        if p.is_empty:
            continue
        if isinstance(p, MultiPolygon) or p.geom_type != "Polygon":
            parts = [g for g in getattr(p, "geoms", [p]) if g.geom_type == "Polygon"]
            if not parts:
                continue
            p = max(parts, key=lambda g: g.area)
        out[i] = p
        taken = p if taken is None else unary_union([taken, p])
    return out


def simplify_room(p, tol=0.08, max_corners=60, min_edge=0.12):
    """A room polygon with no tiny edges and at most max_corners corners, exterior only, counter-clockwise."""
    p = Polygon(p.exterior)
    t = tol
    q = p
    for _ in range(12):
        q = p.simplify(t, preserve_topology=True)
        pts = list(q.exterior.coords)[:-1]
        short = [i for i in range(len(pts)) if math.dist(pts[i], pts[(i + 1) % len(pts)]) < min_edge]
        if len(pts) <= max_corners and not short:
            break
        t *= 1.5
    return q


def doors_between(polys, markers, free_mask, fr, min_w=0.5, max_w=3.0):
    """A door per pair of rooms that touch through free space: a segment on their shared wall.

    Returns [(i, j, (x0, z0), (x1, z1), width_m)] in the same metre frame as polys.
    """
    out = []
    ids = sorted(polys)
    h, w = markers.shape
    # pixels of free space where two different room labels meet (4-neighbourhood)
    pairs = {}
    for dy, dx in ((0, 1), (1, 0)):
        a = markers[: h - dy, : w - dx]
        b = markers[dy:, dx:]
        fa = free_mask[: h - dy, : w - dx] > 0
        fb = free_mask[dy:, dx:] > 0
        sel = (a != b) & fa & fb & (a > 1) & (b > 1)
        ys, xs = np.nonzero(sel)
        for y, x, la, lb in zip(ys, xs, a[sel], b[sel]):
            key = (min(la, lb), max(la, lb))
            pairs.setdefault(key, []).append((x + dx / 2.0, y + dy / 2.0))
    for (i, j), pts in pairs.items():
        if i not in polys or j not in polys:
            continue
        pts = np.array(pts) / fr.res
        if len(pts) < 3:
            continue
        # principal direction of the contact pixels
        c = pts.mean(0)
        u, s, vt = np.linalg.svd(pts - c, full_matrices=False)
        d = vt[0]
        t = (pts - c) @ d
        width = t.max() - t.min() + 1.0 / fr.res
        if width < min_w or width > max_w:
            continue
        shared = polys[i].exterior.intersection(polys[j].exterior)
        if shared.is_empty:
            continue
        lines = [g for g in getattr(shared, "geoms", [shared]) if g.geom_type == "LineString"]
        if not lines:
            continue
        # the shared edge nearest the contact centre
        from shapely.geometry import Point
        ln = min(lines, key=lambda g: g.distance(Point(c)))
        if ln.length < min_w:
            continue
        sp = ln.project(Point(c))
        half = min(width, ln.length) / 2
        a0 = max(0.0, min(sp - half, ln.length - 2 * half))
        a1 = a0 + 2 * half
        # the door must lie wholly on one straight edge: take the straight part of the line
        seg = _straight_part(ln, a0, a1)
        if seg is None:
            continue
        out.append((i, j, seg[0], seg[1], seg[2]))
    return out


def _straight_part(ln, a0, a1):
    """Sub-segment [a0, a1] of a line if it is straight (one edge of the line); else the longest straight piece."""
    pts = list(ln.coords)
    cum = [0.0]
    for p, q in zip(pts, pts[1:]):
        cum.append(cum[-1] + math.dist(p, q))
    best = None
    for k in range(len(pts) - 1):
        lo, hi = max(a0, cum[k]), min(a1, cum[k + 1])
        if hi - lo > 0 and (best is None or hi - lo > best[1] - best[0]):
            best = (lo, hi, k)
    if best is None or best[1] - best[0] < 0.5:
        return None
    lo, hi, k = best
    p, q = pts[k], pts[k + 1]
    L = cum[k + 1] - cum[k]
    f0, f1 = (lo - cum[k]) / L, (hi - cum[k]) / L
    A = (p[0] + (q[0] - p[0]) * f0, p[1] + (q[1] - p[1]) * f0)
    B = (p[0] + (q[0] - p[0]) * f1, p[1] + (q[1] - p[1]) * f1)
    return A, B, hi - lo


# ---------------------------------------------------------------- labels ------------------------------------------
def room_type_of(text):
    """(RoomType name, matched) from a label's words; None when nothing matches."""
    t = text.lower()
    for name, pat in TYPE_WORDS:
        if re.search(pat, t):
            return name
    return None


def label_rooms(polys, words, fr, m_per_pt):
    """Words of the PDF text layer inside each room: {id: text}. Coordinates of polys are metres from the frame origin."""
    labels = {i: [] for i in polys}
    from shapely.geometry import Point
    for x0, y0, x1, y1, t in words:
        cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
        if not (fr.x0 <= cx <= fr.x1 and fr.y0 <= cy <= fr.y1):
            continue
        p = Point((cx - fr.x0) * m_per_pt, (cy - fr.y0) * m_per_pt)
        for i, g in polys.items():
            if g.contains(p):
                labels[i].append((round(cy, 1), x0, t))
                break
    out = {}
    for i, ws in labels.items():
        if ws:
            ws.sort()
            out[i] = " ".join(w[2] for w in ws)
    return out


def simplify_coverage(polys, tol=0.1):
    """Simplify all rooms together so that shared walls stay shared (shapely's coverage simplification)."""
    import shapely
    ids = list(polys)
    try:
        out = shapely.coverage_simplify([polys[i] for i in ids], tol)
    except Exception:
        out = [simplify_room(polys[i]) for i in ids]
    res = {}
    for i, g in zip(ids, out):
        if g is None or g.is_empty:
            continue
        if g.geom_type != "Polygon":
            parts = [x for x in getattr(g, "geoms", []) if x.geom_type == "Polygon"]
            if not parts:
                continue
            g = max(parts, key=lambda x: x.area)
        res[i] = Polygon(g.exterior)
    return res


def entrances(polys, markers, walls, outline, fr, min_w=0.8, max_w=3.5):
    """Gaps in the outer wall: a room's free space meeting the outside without a wall between. [(room, a, b, width)]."""
    h, w = markers.shape
    free_in = (walls == 0) & (outline > 0)
    free_out = (walls == 0) & (outline == 0)
    pts = {}
    for dy, dx in ((0, 1), (1, 0)):
        a_in, b_in = free_in[: h - dy, : w - dx], free_in[dy:, dx:]
        a_out, b_out = free_out[: h - dy, : w - dx], free_out[dy:, dx:]
        for sel, lab in ((a_in & b_out, markers[: h - dy, : w - dx]), (b_in & a_out, markers[dy:, dx:])):
            ys, xs = np.nonzero(sel)
            for y, x, l in zip(ys, xs, lab[sel]):
                pts.setdefault(int(l), []).append((x + dx / 2.0 + 0.5, y + dy / 2.0 + 0.5))
    out = []
    from shapely.geometry import Point
    for i, plist in pts.items():
        if i not in polys:
            continue
        pa = np.array(plist) / fr.res
        # split into clusters of touching contact pixels
        order = np.argsort(pa[:, 0] * 1000 + pa[:, 1])
        pa = pa[order]
        clusters, cur = [], [pa[0]]
        for q in pa[1:]:
            if min(np.hypot(*(q - c)) for c in cur[-12:]) <= 0.35:
                cur.append(q)
            else:
                clusters.append(cur)
                cur = [q]
        clusters.append(cur)
        for cl in clusters:
            cl = np.array(cl)
            if len(cl) < 3:
                continue
            c = cl.mean(0)
            u, s, vt = np.linalg.svd(cl - c, full_matrices=False)
            t = (cl - c) @ vt[0]
            width = t.max() - t.min() + 1.0 / fr.res
            if width < min_w or width > max_w:
                continue
            ring = polys[i].exterior
            sp = ring.project(Point(c))
            half = min(width, ring.length) / 2
            seg = _straight_part(ring, max(0.0, sp - half), sp + half) if ring.length > 2 * half else None
            if seg is None:
                continue
            out.append((i, seg[0], seg[1], seg[2]))
    return out


def _is_red(c):
    return c is not None and len(c) == 3 and c[0] > 0.8 and c[1] < 0.3 and c[2] < 0.2


def red_arrows(drawings, clip=None):
    """Red arrow symbols (a stem and a filled head): [(tip (x, y) pt, direction (dx, dy))]. Entrances, exits and stairs share them."""
    stems, heads = [], []
    for d in drawings:
        r = d["rect"]
        if clip and not (r.x1 > clip[0] and r.x0 < clip[2] and r.y1 > clip[1] and r.y0 < clip[3]):
            continue
        ls = [(tuple(i[1]), tuple(i[2])) for i in d["items"] if i[0] == "l"]
        if d.get("type") == "s" and _is_red(d.get("color")) and len(ls) == 1 and (d.get("width") or 0) > 0.5:
            stems.append(ls[0])
        elif d.get("type") in ("f", "fs") and _is_red(d.get("fill")) and len(d["items"]) == 3 and len(ls) == 3:
            heads.append([ls[0][0], ls[1][0], ls[2][0]])
    out = []
    for tri in heads:
        cx = sum(p[0] for p in tri) / 3
        cy = sum(p[1] for p in tri) / 3
        best = None
        for a, b in stems:
            for near, far in ((a, b), (b, a)):
                dd = math.dist(near, (cx, cy))
                if dd < 6 and (best is None or dd < best[0]):
                    best = (dd, near, far)
        if not best:
            continue
        far = best[2]
        tip = max(tri, key=lambda p: math.dist(p, far))
        v = (tip[0] - far[0], tip[1] - far[1])
        n = math.hypot(*v) or 1.0
        out.append((tip, (v[0] / n, v[1] / n)))
    return out

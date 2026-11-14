"""Orientation plans (#1572): the inside of one building drawn for visitors, as flat colour fills.

A visitor or wayfinding plan has no scale bar and draws rooms as filled areas, not wall lines, with names, entrances, stairs, lifts and
toilets. Nothing here reads walls. The page is rendered, its colours quantised, and each colour's connected regions (after an opening
that removes text and line work) are the rooms; the paper, banners that touch the page border, small icons (a region mostly inside one
other) and anything outside the main cluster of rooms (a legend) are dropped. Names are the words of the PDF text layer whose
centre lies in a room (a raster page has no text layer, so its rooms are unnamed: there is no OCR here). Stairs, lifts, toilets and
entrances are marked only where a word says so. Nothing is invented: no door, no room, no dimension.

`extract` returns the rooms in render pixels; `topology` turns them into a scale-free description (relative areas, adjacency,
entrances on the outline) used when the scale gate fails.
"""
import math
import re

import cv2
import numpy as np
from shapely.geometry import Point, Polygon
from shapely.ops import unary_union

import extract as ex
import pdfio

LONG_PX = 2000          # the page is rendered with this long side
PAL_MIN = 0.002         # a palette colour covers at least this share of the page
MERGE_DE = 6.0          # palette colours closer than this (Lab) are one colour
ASSIGN_DE = 20.0        # a pixel farther than this from every palette colour belongs to none
OPEN_FRAC = 0.0035      # opening kernel (share of the long side): removes text strokes and line work
MIN_FRAC = 0.00035      # smallest room, share of the page area
HOLE_FRAC = 0.0015      # unclaimed specks smaller than this inside rooms (text, icons) are absorbed by the room around them
CLUSTER_FRAC = 0.02     # rooms closer than this (share of the long side) belong to one drawing
GAP_FRAC = 0.012        # closing for the outline: the gap between rooms (a wall, a white line)
ICON_ROUND = 0.84       # circularity above which a small region is a round badge (a square is 0.785)
ICON_RATIO = 6.0        # a region under this many times the smallest room that lies mostly inside another is an icon
MIN_ROOMS = 3
LEADER_MAX_W = 1.6      # stroke width (pt) of a leader line

ENTRANCE = r"eingang|entr[ée]e|entrance|accueil|ingresso|\bzugang|haupteingang"
STAIRS = r"treppe|stairs|staircase|escalier|scala\b|treppenhaus"
LIFT = r"\blifts?\b|aufzug|ascenseur|elevator|ascensore"
WC = r"\bwc\b|toilet|toilette|damen|herren|restroom|\bw\.c\."
MARKS = (("entrance", ENTRANCE), ("stairs", STAIRS), ("lift", LIFT), ("wc", WC))


# ------------------------------------------------------------------------------------------------ colours -> regions
def palette(rgb, max_colours=48):
    """Dominant colours of an RGB image: (centres in Lab (K, 3) float32, centres in RGB (K, 3)). Greedy merge of 4-bit bins by count."""
    flat = rgb.reshape(-1, 3)
    q = flat >> 4
    key = (q[:, 0].astype(np.int32) << 8) | (q[:, 1].astype(np.int32) << 4) | q[:, 2].astype(np.int32)
    cnt = np.bincount(key, minlength=4096).astype(np.float64)
    sums = np.stack([np.bincount(key, weights=flat[:, c], minlength=4096) for c in range(3)], 1)
    bins = [k for k in np.argsort(-cnt) if cnt[k] >= PAL_MIN * key.size]
    mean_rgb = (sums[bins] / cnt[bins][:, None]).astype(np.float32)
    lab = cv2.cvtColor((mean_rgb / 255.0).reshape(1, -1, 3), cv2.COLOR_RGB2LAB).reshape(-1, 3)
    centres, weights = [], []
    for c, w, rgb_c in zip(lab, cnt[bins], mean_rgb):
        for i, o in enumerate(centres):
            if np.linalg.norm(c - o[0]) < MERGE_DE:
                tot = weights[i] + w
                centres[i] = ((o[0] * weights[i] + c * w) / tot, (o[1] * weights[i] + rgb_c * w) / tot)
                weights[i] = tot
                break
        else:
            if len(centres) < max_colours:
                centres.append((c, rgb_c))
                weights.append(w)
    return np.array([c[0] for c in centres], np.float32), np.array([c[1] for c in centres])


def classify_pixels(rgb, lab_c):
    """Palette index per pixel (-1 where no palette colour is within ASSIGN_DE)."""
    lab = cv2.cvtColor(rgb.astype(np.float32) / 255.0, cv2.COLOR_RGB2LAB).reshape(-1, 3)
    out = np.empty(len(lab), np.int16)
    for s in range(0, len(lab), 400000):
        d = ((lab[s:s + 400000, None, :] - lab_c[None, :, :]) ** 2).sum(-1)
        k = d.argmin(1)
        out[s:s + 400000] = np.where(d[np.arange(len(k)), k] <= ASSIGN_DE ** 2, k, -1)
    return out.reshape(rgb.shape[:2])


def paper_colour(idx):
    """The palette index that fills the page border (the paper), or -1: the colour of at least 40 % of a frame round the image."""
    h, w = idx.shape
    t = max(2, int(0.015 * max(h, w)))
    ring = np.concatenate([idx[:t].ravel(), idx[-t:].ravel(), idx[:, :t].ravel(), idx[:, -t:].ravel()])
    ring = ring[ring >= 0]
    if ring.size == 0:
        return -1
    cnt = np.bincount(ring)
    return int(cnt.argmax()) if cnt.max() >= 0.4 * ring.size else -1


def is_paper(lab_c):
    """Pure white, whatever the border says."""
    L, a, b = lab_c
    return L > 98 and math.hypot(a, b) < 3


def regions(rgb, log=None):
    """Colour regions of a page image: (rid int32 map, list of dict(id, colour, area_px)). rid 0 is no room."""
    h, w = rgb.shape[:2]
    long = max(h, w)
    lab_c, rgb_c = palette(rgb)
    idx = classify_pixels(rgb, lab_c)
    paper = paper_colour(idx)
    op = max(3, int(round(OPEN_FRAC * long))) | 1
    kern = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (op, op))
    min_px = MIN_FRAC * h * w
    rid = np.zeros((h, w), np.int32)
    info = []
    for k in range(len(lab_c)):
        if k == paper or (is_paper(lab_c[k]) and (paper < 0 or is_paper(lab_c[paper]))):  # white is paper unless the paper is another colour
            continue
        m = cv2.morphologyEx((idx == k).astype(np.uint8), cv2.MORPH_OPEN, kern)
        if m.sum() < min_px:
            continue
        n, lbl, st, _ = cv2.connectedComponentsWithStats(m, connectivity=4)
        for j in range(1, n):
            x, y, bw, bh, area = st[j]
            if area < min_px:
                continue
            touches = x <= 1 or y <= 1 or x + bw >= w - 1 or y + bh >= h - 1
            if touches and (bw >= 0.85 * w or bh >= 0.85 * h):  # a banner or a ground colour, not a room
                continue
            rid[lbl == j] = len(info) + 1
            info.append(dict(id=len(info) + 1, colour=[int(v) for v in rgb_c[k]], area_px=int(area)))
    rid, info = drop_icons(rid, info, min_px)
    rid = absorb_specks(rid, long, h * w)
    return rid, info


def drop_icons(rid, info, min_px):
    """Small regions lying mostly inside one other region are pictograms or numbered discs, not rooms."""
    k3 = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (7, 7))
    for r in list(info):
        if r["area_px"] > ICON_RATIO * min_px:
            continue
        m = (rid == r["id"]).astype(np.uint8)
        cs, _ = cv2.findContours(m, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
        if cs:
            big = max(cs, key=cv2.contourArea)
            per = cv2.arcLength(big, True)
            if per > 0 and 4 * math.pi * cv2.contourArea(big) / per ** 2 > ICON_ROUND:  # a disc: a pictogram's badge
                rid[rid == r["id"]] = 0
                info.remove(r)
                continue
        ring = cv2.dilate(m, k3) & (1 - m)
        around = rid[ring > 0]
        around = around[around != r["id"]]
        if around.size == 0:
            continue
        ids, cnt = np.unique(around, return_counts=True)
        top = ids[cnt.argmax()]
        if top > 0 and cnt.max() >= 0.8 * ring.sum() and next(q for q in info if q["id"] == top)["area_px"] > 2 * r["area_px"]:
            rid[rid == r["id"]] = 0
            info.remove(r)
    return rid, info


def absorb_specks(rid, long, area):
    """Small unclaimed specks (the letters of a label, a pictogram's strokes) take the room around them; the paper and the gaps
    between rooms are one big unclaimed area and stay unclaimed."""
    free = (rid == 0).astype(np.uint8)
    n, lbl, st, _ = cv2.connectedComponentsWithStats(free, connectivity=8)
    small = np.zeros(rid.shape, bool)
    lim = HOLE_FRAC * area
    for j in range(1, n):
        if st[j, cv2.CC_STAT_AREA] < lim:
            small |= lbl == j
    f = rid.astype(np.float32)
    k3 = np.ones((3, 3), np.uint8)
    for _ in range(max(6, int(0.01 * long))):
        pending = small & (f == 0)
        if not pending.any():
            break
        d = cv2.dilate(f, k3)
        f = np.where(pending & (d > 0), d, f)
    return f.astype(np.int32)


# ------------------------------------------------------------------------------------------------ polygons
def _poly_of(mask, eps):
    cs, hier = cv2.findContours(mask, cv2.RETR_CCOMP, cv2.CHAIN_APPROX_SIMPLE)
    if not cs:
        return None
    best = max((i for i in range(len(cs)) if hier[0][i][3] < 0), key=lambda i: cv2.contourArea(cs[i]))
    shell = cv2.approxPolyDP(cs[best], eps, True).reshape(-1, 2)
    if len(shell) < 3:
        return None
    holes = []
    for i in range(len(cs)):
        if hier[0][i][3] == best and cv2.contourArea(cs[i]) > 40:
            hp = cv2.approxPolyDP(cs[i], eps, True).reshape(-1, 2)
            if len(hp) >= 3:
                holes.append(hp)
    g = Polygon(shell, holes)
    if not g.is_valid:
        g = g.buffer(0)
    if g.geom_type != "Polygon":
        parts = [p for p in getattr(g, "geoms", []) if p.geom_type == "Polygon"]
        if not parts:
            return None
        g = max(parts, key=lambda p: p.area)
    return g


def cluster(rid, long):
    """Rooms of the biggest drawing: the connected cluster (rooms closer than CLUSTER_FRAC of the page) with the most room area."""
    claimed = (rid > 0).astype(np.uint8)
    r = max(3, int(CLUSTER_FRAC * long))
    near = cv2.dilate(claimed, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (r, r)))
    n, lbl, _, _ = cv2.connectedComponentsWithStats(near, connectivity=8)
    if n <= 1:
        return rid, 0
    areas = [(claimed[lbl == j].sum(), j) for j in range(1, n)]
    keep = max(areas)[1]
    out = np.where((lbl == keep), rid, 0).astype(np.int32)
    return out, n - 1


def outline_of(rid, long):
    """The union of the rooms, closed over the gaps between them: one polygon in pixels."""
    g = max(3, int(GAP_FRAC * long)) | 1
    m = cv2.morphologyEx((rid > 0).astype(np.uint8), cv2.MORPH_CLOSE, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (g, g)))
    cs, _ = cv2.findContours(m, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    if not cs:
        return None
    big = max(cs, key=cv2.contourArea)
    pts = cv2.approxPolyDP(big, 0.002 * long, True).reshape(-1, 2)
    p = Polygon(pts)
    if not p.is_valid:
        p = p.buffer(0)
    if p.geom_type != "Polygon":
        p = max((g for g in getattr(p, "geoms", []) if g.geom_type == "Polygon"), key=lambda g: g.area, default=None)
    return p


# ------------------------------------------------------------------------------------------------ names
def text_blocks(words, ppp, x0, y0):
    """Words (page points) grouped into labels: words on one line, then lines stacked closely. Returns dict(text, c=(x, y) px, h px)."""
    ws = sorted(((w[1] + w[3]) / 2, w[0], w[2], w[1], w[3], w[4]) for w in words if w[4].strip())
    ws.sort(key=lambda t: (t[1], t[0]))
    lines = []
    for cy, xa, xb, ya, yb, t in ws:
        h = yb - ya
        for ln in lines:
            if abs(ln["cy"] - cy) < 0.45 * max(h, ln["h"]) and 0 <= xa - ln["xb"] < 1.3 * max(h, ln["h"]) and xa >= ln["xb"] - 1:
                ln["words"].append(t)
                ln["xb"] = xb
                ln["ya"], ln["yb"] = min(ln["ya"], ya), max(ln["yb"], yb)
                break
        else:
            lines.append(dict(cy=cy, xa=xa, xb=xb, ya=ya, yb=yb, h=h, words=[t]))
    blocks = []
    for ln in sorted(lines, key=lambda l: l["cy"]):
        for b in blocks:
            gap = ln["ya"] - b["yb"]
            cx_b, cx_l = (b["xa"] + b["xb"]) / 2, (ln["xa"] + ln["xb"]) / 2
            if 0 <= gap < 0.7 * max(ln["h"], b["h"]) and abs(cx_b - cx_l) < max(0.9 * (b["xb"] - b["xa"]), 1.6 * ln["h"]) and abs(ln["h"] - b["h"]) < 0.4 * b["h"]:
                b["lines"].append(" ".join(ln["words"]))
                b["xa"], b["xb"] = min(b["xa"], ln["xa"]), max(b["xb"], ln["xb"])
                b["yb"] = ln["yb"]
                break
        else:
            blocks.append(dict(xa=ln["xa"], xb=ln["xb"], ya=ln["ya"], yb=ln["yb"], h=ln["h"], lines=[" ".join(ln["words"])]))
    return [dict(text=" ".join(b["lines"]), c=((b["xa"] + b["xb"]) / 2 - x0) * ppp, cy=((b["ya"] + b["yb"]) / 2 - y0) * ppp,
                 h=b["h"] * ppp, box=((b["xa"] - x0) * ppp, (b["ya"] - y0) * ppp, (b["xb"] - x0) * ppp, (b["yb"] - y0) * ppp)) for b in blocks]


def leader_lines(path, page):
    """Thin stroked line segments of a PDF page (page points): the leader lines that tie a label beside the plan to its room."""
    out = []
    for d in pdfio.open_doc(path)[page - 1].get_cdrawings():
        if d.get("type") != "s" or (d.get("width") or 0) > LEADER_MAX_W:
            continue
        out += [(it[1][0], it[1][1], it[2][0], it[2][1]) for it in d["items"] if it[0] == "l"]
    return out


def leader_homes(rooms, blocks, lines_px, long):
    """{block index: room} for labels that lie outside every room but hang on a leader line (a chain of thin segments) whose
    far end is in one room. A chain that reaches two rooms, or none, attaches nothing."""
    from shapely.geometry import LineString, Point as P, box
    from shapely.ops import linemerge, unary_union
    segs = [LineString([(a, b), (c, d)]) for a, b, c, d in lines_px if math.hypot(c - a, d - b) > 3]
    if not segs:
        return {}
    u = unary_union(segs)
    merged = linemerge(u) if u.geom_type == "MultiLineString" else u
    chains = [g for g in getattr(merged, "geoms", [merged]) if g.geom_type == "LineString" and not g.is_ring and g.length < 0.45 * long]
    homes = {}
    for bi, b in blocks.items():
        h = b["h"]
        bx = box(b["box"][0] - 0.8 * h, b["box"][1] - 0.8 * h, b["box"][2] + 0.8 * h, b["box"][3] + 0.8 * h)
        found = set()
        for ch in chains:
            if not bx.intersects(ch):
                continue
            for e in (P(ch.coords[0]), P(ch.coords[-1])):
                if e.distance(bx) <= 2 * h:
                    continue
                for r in rooms:
                    if r["_poly"].distance(e) <= 0.006 * long:
                        found.add(r["id"])
        if len(found) == 1:
            homes[bi] = next(r for r in rooms if r["id"] in found)
    return homes


def name_rooms(rooms, blocks, outline, long, lines_px=()):
    """Attach each text block to the room that holds its centre (or that its leader line reaches), and find the marks
    (entrance, stairs, lift, wc)."""
    marks = []
    homes = {}
    for i, b in enumerate(blocks):
        pt = Point(b["c"], b["cy"])
        homes[i] = next((r for r in rooms if r["_poly"].contains(pt)), None)
    if len(lines_px):
        homes.update(leader_homes(rooms, {i: blocks[i] for i, r in homes.items() if r is None}, lines_px, long))
    for i, b in enumerate(blocks):
        pt = Point(b["c"], b["cy"])
        home = homes[i]
        low = b["text"].lower()
        kinds = [k for k, pat in MARKS if re.search(pat, low)]
        if home is not None:
            home.setdefault("_texts", []).append(b["text"])
            for k in kinds:
                home["features"] = sorted(set(home.get("features", [])) | {k})
        for k in kinds:
            at = None
            if k == "entrance" and outline is not None and outline.exterior.distance(pt) <= 0.08 * long:
                q = outline.exterior.interpolate(outline.exterior.project(pt))
                at = [q.x, q.y]
            if home is None and at is None:
                continue  # a word of the legend or of the street: not part of the building
            marks.append(dict(kind=k, text=b["text"], room=None if home is None else home["id"], at_px=at, text_px=[b["c"], b["cy"]]))
    for r in rooms:
        ts = r.pop("_texts", [])
        r["label"] = " ".join(ts) if ts else None
        r["type"] = ex.room_type_of(r["label"]) if r["label"] else None
        r["named"] = bool(r["label"] and re.search(r"[^\W\d_]{3}", r["label"]))
    return marks


# ------------------------------------------------------------------------------------------------ extract
def render(root_path, page, region=None, long_px=LONG_PX):
    """(RGB image, px per page point, clip origin in points, words in page points) of a page or region of it."""
    import features
    import pymupdf
    doc = pdfio.open_doc(root_path)
    pg = doc[page - 1]
    clip = pymupdf.Rect(*region) if region else pg.rect
    ppp = long_px / max(clip.width, clip.height)
    img = pdfio.render(pg, scale=ppp, clip=clip if region else None)
    words = features.words_of(pg)
    return img, ppp, (clip.x0, clip.y0), words


def extract(img, ppp, origin=(0.0, 0.0), words=(), lines=()):
    """Rooms, outline and marks of one orientation-plan image. Returns dict(ok, why, rooms, outline, marks, ppp, ...) in pixels."""
    h, w = img.shape[:2]
    long = max(h, w)
    rid, info = regions(img)
    rid, n_clusters = cluster(rid, long)
    ids = [r["id"] for r in info if (rid == r["id"]).any()]
    eps = 0.0015 * long
    rooms = []
    for i in ids:
        g = _poly_of((rid == i).astype(np.uint8), eps)
        if g is None or g.area < MIN_FRAC * h * w:
            continue
        r = next(q for q in info if q["id"] == i)
        rooms.append(dict(id=len(rooms), colour=r["colour"], area_px=float(g.area), _poly=g, poly_px=[list(map(float, p)) for p in g.exterior.coords[:-1]],
                          holes_px=[[list(map(float, p)) for p in hl.coords[:-1]] for hl in g.interiors], features=[]))
    res = dict(ok=False, why=None, rooms=rooms, outline=None, marks=[], ppp=ppp, origin=origin, size=(w, h), n_clusters=n_clusters, n_colours=len(info))
    if len(rooms) < MIN_ROOMS:
        res["why"] = "%d colour regions found as rooms (needs %d)" % (len(rooms), MIN_ROOMS)
        return res
    outline = outline_of(rid, long)
    if outline is None or outline.is_empty:
        res["why"] = "no outline from the union of the rooms"
        return res
    res["outline"] = outline
    blocks = text_blocks(words, ppp, origin[0], origin[1]) if len(words) else []
    lines_px = [((a - origin[0]) * ppp, (b - origin[1]) * ppp, (c - origin[0]) * ppp, (d - origin[1]) * ppp) for a, b, c, d in lines]
    res["marks"] = name_rooms(rooms, blocks, outline, long, lines_px)
    res["n_text_blocks"] = len(blocks)
    res["ok"] = True
    return res


# ------------------------------------------------------------------------------------------------ topology
ADJ_MIN = 0.02  # contact length, share of the plan's longest side, under which two rooms are not adjacent


def topology(res):
    """A scale-free description: relative areas, adjacency, entrances on the outline. Coordinates are fractions of the plan's longest side;
    no dimension in metres anywhere."""
    rooms = res["rooms"]
    x0, y0, x1, y1 = res["outline"].bounds
    S = max(x1 - x0, y1 - y0)
    gap = GAP_FRAC * max(res["size"]) * 0.6

    def rel(p):
        return [round((p[0] - x0) / S, 4), round((p[1] - y0) / S, 4)]
    tot = sum(r["area_px"] for r in rooms)
    adj = []
    grown = [r["_poly"].buffer(gap) for r in rooms]
    for i in range(len(rooms)):
        for j in range(i + 1, len(rooms)):
            if not grown[i].intersects(grown[j]):
                continue
            contact = grown[i].intersection(grown[j]).area / (2 * gap) / S
            if contact >= ADJ_MIN:
                adj.append(dict(a=i, b=j, contact=round(contact, 3)))
    ents = []
    for m in res["marks"]:
        if m["kind"] == "entrance" and m["at_px"]:
            ents.append(dict(room=m["room"], at=rel(m["at_px"]), label=m["text"]))
    return dict(kind="topology_only", units="fractions of the plan's longest side; no metres",
                outline=[rel(p) for p in res["outline"].exterior.coords[:-1]],
                rooms=[dict(id=r["id"], label=r["label"], type=r["type"], features=r["features"], area_share=round(r["area_px"] / tot, 4),
                            centre=rel(r["_poly"].representative_point().coords[0]), poly=[rel(p) for p in r["poly_px"]]) for r in rooms],
                adjacency=adj, entrances=ents,
                marks=[dict(kind=m["kind"], text=m["text"], room=m["room"]) for m in res["marks"]])


def summary(res):
    """Counts for the report: rooms, labelled, named, marks."""
    rooms = res["rooms"]
    return dict(rooms=len(rooms), labelled=sum(1 for r in rooms if r["label"]), named=sum(1 for r in rooms if r["named"]),
                names=[r["label"] for r in rooms if r["named"]][:30], marks={k: sum(1 for m in res["marks"] if m["kind"] == k) for k, _ in MARKS},
                clusters=res["n_clusters"])

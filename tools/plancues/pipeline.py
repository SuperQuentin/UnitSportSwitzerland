"""Stages scale -> extract -> place on one page (or one region of a sheet); each loads and saves the cue file."""
import json
import math
import os

import cv2
import numpy as np
from shapely.geometry import MultiPolygon, Polygon
from shapely.ops import unary_union

import aerial
import cue as cuem
import extract as ex
import features
import footscale
import northarrow
import pdfio
import place
import scale as scalem
import tiles

HERE = os.path.dirname(os.path.abspath(__file__))
GRASS_MARGIN_M = 2.0  # grass seen beyond each touchline and goal line of the drawn pitch


def venues():
    with open(os.path.join(HERE, "venues.json"), encoding="utf-8") as f:
        return json.load(f)


def venue_info(venue, at=None, kind=None):
    v = dict(venues().get(venue, {}))
    if at:
        v["at"] = list(at)
    if kind:
        v["kind"] = kind
    v.setdefault("kind", "building")
    return v


class Page:
    """A page of a PDF with its vector content read once."""

    def __init__(self, root, rel_path, page):
        self.rel = rel_path
        self.no = page
        self.doc = pdfio.open_doc(os.path.join(root, rel_path))
        self.page = self.doc[page - 1]
        self.stats, self.drawings = features.vector_stats(self.page)
        self.words = features.words_of(self.page)
        self.rect = self.page.rect


def _clip_of(cue_, rect):
    b = cue_["sources"][0]["bbox"]
    return None if b is None or (b[0] <= 0 and b[1] <= 0 and b[2] >= rect.width - 1 and b[3] >= rect.height - 1) else tuple(b)


# ------------------------------------------------------------------------------------------------ scale
def scale_stage(root, venue, rel_path, page, region=None, kinds=("pitch",)):
    pg = Page(root, rel_path, page)
    bbox = region or (0.0, 0.0, pg.rect.width, pg.rect.height)
    c = cuem.new_cue(venue, rel_path, page, bbox, "vector" if pg.stats["n_paths"] > 30 else "raster")
    clip = _clip_of(c, pg.rect)
    res = scalem.decide(pg.words, pg.drawings, pg.rect, clip, kinds)
    if res["m_per_pt"] is None:
        c["scale"] = None
        cuem.set_status(c, "rejected", ["no scale: no graphic scale bar and no known object (pitch) found"])
        return c, pg
    cc = None
    reasons = []
    status = "ok"
    if res["cross_check"]:
        cc = res["cross_check"]
        if abs(cc["ratio"] - 1) > 0.03:
            reasons.append("scale bar and %s disagree by %.1f %%" % (cc["kind"], 100 * (cc["ratio"] - 1)))
            status = "low_confidence"
    import sortpages
    votes = sortpages.keyword_votes(" ".join(w[4] for w in pg.words), os.path.basename(rel_path))
    top = max(votes, key=votes.get) if votes else None
    walls = ex.stroke_classes(pg.drawings)["wall_widths"]
    if top in ("access_map", "site_plan") and votes[top] >= 4 and (not cc or abs(cc["ratio"] - 1) > 0.03):
        cuem.set_status(c, "rejected", ["schematic %s: the page says %s and no scale bar agrees with a known object, so it is not geometry"
                                        % (top.replace("_", " "), top.replace("_", " ")),
                                        "wall weights found: %s" % (walls or "none")])
        c["scale"] = None
        return c, pg
    c["scale"] = dict(m_per_pt=res["m_per_pt"], method=res["method"], cross_check=cc)
    if res["bar"]:
        c["scale"]["bar"] = dict(labels=res["bar"]["labels"], residual_m=res["bar"]["resid_m"], anchor=res["bar"]["anchor"])
    if res["method"] == "known_object" and not cc:
        status = "low_confidence"
        reasons.append("scale only from a known object, no scale bar to confirm it")
    cuem.set_status(c, status, reasons)
    return c, pg


# ------------------------------------------------------------------------------------------------ scale from the footprint
FOOT_RADIUS_M = 120.0   # buildings considered around the venue hint
FOOT_CANDIDATES = 12
_cand_cache = {}


def footprint_candidates(chunks, at, radius=FOOT_RADIUS_M, limit=FOOT_CANDIDATES):
    """The buildings near a hint that a plan could be of: the `limit` nearest solids over 30 m2."""
    from shapely.geometry import Point
    key = (chunks, round(at[0]), round(at[1]), radius)
    if key not in _cand_cache:
        pt = Point(at[0], at[1])
        bs = [b for b in tiles.buildings_near(chunks, at[0], at[1], radius) if b["fp"].area > 30]
        bs.sort(key=lambda b: (b["fp"].distance(pt), -b["fp"].area))
        _cand_cache[key] = bs[:limit]
    return _cand_cache[key]


def footscale_stage(root, venue, rel_path, page, chunks, info, region=None, thr=None, bias=None, refs=True, final=True,
                    cache_dir=None, log=print):
    """A vector page with no bar and no known object, scaled to the building's footprint when the two clearly share a shape.

    The outline is extracted at a provisional scale (the scale found is fed back until it settles, at most 3 passes), searched
    against the nearby footprints (`footscale.judge`) and, if the gate passes, the page is extracted at the scale found and placed
    on the building chosen. Returns (cue, page, judgement); the judgement carries the numbers even when the gate rejects.
    """
    pg = Page(root, rel_path, page)
    bbox = region or (0.0, 0.0, pg.rect.width, pg.rect.height)
    c = cuem.new_cue(venue, rel_path, page, bbox, "vector" if pg.stats["n_paths"] > 30 else "raster")
    j = None
    at = info.get("at")
    if not at:
        cuem.set_status(c, "rejected", ["no map position hint for this venue (--at E,N)"])
        return c, pg, j
    cands = footprint_candidates(chunks, at)
    if not cands:
        cuem.set_status(c, "rejected", ["no building solid near the position hint in the tiles"])
        return c, pg, j
    m = 0.15
    for _ in range(3):
        c0 = cuem.new_cue(venue, rel_path, page, bbox, "vector")
        c0["scale"] = dict(m_per_pt=m, method="provisional", cross_check=None)
        extract_stage(c0, pg, "building", outline_only=True)
        if not c0["levels"]:
            cuem.set_status(c, "rejected", ["footprint scale: " + "; ".join(r for r in c0["reasons"] if r != "not processed")])
            return c, pg, j
        poly = place.scaled(Polygon(c0["levels"][0]["outline"]), 1.0 / m)
        j = footscale.judge(poly, cands, thr, bias, words=pg.words, refs=refs)
        new = j.get("raw_m_per_pt") or m
        done = abs(new / m - 1) < 0.15
        m = new
        if done:
            break
    j["passes_m_per_pt"] = m
    if j["status"] == "rejected":
        c["scale"] = None
        cuem.set_status(c, "rejected", ["no scale: no bar, no known object, and the footprint does not clearly match"] + j["reasons"])
        return c, pg, j
    cc = j["cross_check"] if j["cross_check"] and j["cross_check"]["agrees"] else None
    c["scale"] = dict(m_per_pt=j["m_per_pt"], method="footprint_fit", cross_check=cc,
                      footprint=dict(egid=j["egid"], iou=round(j["iou"], 4), rms_m=round(j["rms_m"], 3), margin=None if j["margin"] is None else round(j["margin"], 4),
                                     distinct=None if j.get("distinct") is None else round(j["distinct"], 4), raw_m_per_pt=j["raw_m_per_pt"],
                                     bias=footscale.BIAS if bias is None else bias))
    cuem.set_status(c, j["status"], j["reasons"])
    if final:
        extract_stage(c, pg, "building", debug_dir=None)
        if c["levels"]:
            info2 = dict(info, kind="building")
            place_stage(c, chunks, cache_dir or "", info2, egid=j["egid"], log=log)
    return c, pg, j


# ------------------------------------------------------------------------------------------------ stadium facts
def _dashed_lines(pg, minlen=100.0):
    hs, vs = [], []
    for d in pg.drawings:
        if not d.get("dashes") or d["dashes"] in ("[] 0", "") or len(d["items"]) != 1 or d["items"][0][0] != "l":
            continue
        a, b = d["items"][0][1], d["items"][0][2]
        if abs(a[1] - b[1]) < 0.05 and abs(a[0] - b[0]) >= minlen:
            hs.append((float(a[1]), float(min(a[0], b[0])), float(max(a[0], b[0]))))
        elif abs(a[0] - b[0]) < 0.05 and abs(a[1] - b[1]) >= minlen:
            vs.append((float(a[0]), float(min(a[1], b[1])), float(max(a[1], b[1]))))
    return hs, vs


def stadium_facts(pg, c, m):
    """Pitch, seating front and roof outline from the drawing's own lines, in plan metres from the pitch centre."""
    objs = scalem.find_known_objects(pg.drawings, pg.rect, ("pitch",))
    if not objs:
        return None
    o = objs[0]
    x0, y0, x1, y1 = o["rect"]
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    out = dict(pitch_rect_pt=[x0, y0, x1, y1], pitch_centre_pt=[cx, cy],
               pitch_length_m=(x1 - x0) * m, pitch_width_m=(y1 - y0) * m, pitch_horizontal=o["horizontal"])
    hs, vs = _dashed_lines(pg)
    # nearest dashed line beyond each touchline / goal line = the seating front; the farthest = the roof's outer edge
    def pick(lines, lo_pos, hi_pos, side_lo):
        inner_lo = max([l for l in lines if l[0] < lo_pos - 1], key=lambda l: l[0], default=None)
        inner_hi = min([l for l in lines if l[0] > hi_pos + 1], key=lambda l: l[0], default=None)
        outer_lo = min(lines, key=lambda l: l[0], default=None)
        outer_hi = max(lines, key=lambda l: l[0], default=None)
        return inner_lo, inner_hi, outer_lo, outer_hi
    top = pick(hs, y0, y1, True)
    left = pick(vs, x0, x1, True)
    if None in top or None in left:
        out["seating_front"] = None
        return out
    out["seating_front_pt"] = [left[0][0], top[0][0], left[1][0], top[1][0]]  # x0, y0, x1, y1 (inner dashed lines)
    out["roof_outer_pt"] = [left[2][0], top[2][0], left[3][0], top[3][0]]
    return out


# ------------------------------------------------------------------------------------------------ extract
def _origin_and_shift(poly_clip_m):
    minx, miny, maxx, maxy = poly_clip_m.bounds
    return (minx + maxx) / 2, (miny + maxy) / 2


def extract_stage(c, pg, venue_kind="building", close_m=None, res=ex.RES, debug_dir=None, outline_only=False):
    m = c["scale"]["m_per_pt"]
    clip = _clip_of(c, pg.rect) or (0, 0, pg.rect.width, pg.rect.height)
    sel = [d for d in pg.drawings if d["rect"].x1 > clip[0] and d["rect"].x0 < clip[2] and d["rect"].y1 > clip[1] and d["rect"].y0 < clip[3]]
    classes = ex.stroke_classes(sel)
    if not classes["wall_widths"]:
        cuem.set_status(c, "rejected", ["no wall weight: the page has no heavy dark stroke class (looks schematic or not a cut plan)"])
        return c
    strokes, solids = ex.select_wall_paths(sel, m, classes, clip)
    # keep the raster inside a sane size
    while ((clip[2] - clip[0]) * m * res) * ((clip[3] - clip[1]) * m * res) > 30e6:
        res *= 0.75
    fr = ex.Frame(clip, m, res)
    mask = ex.clean_mask(ex.rasterize(strokes, solids, fr), res)
    if mask.sum() == 0:
        cuem.set_status(c, "rejected", ["no walls found"])
        return c
    stadium = venue_kind == "stadium"
    if stadium:
        pts = cv2.findNonZero(mask)
        hull = cv2.convexHull(pts)
        ol = np.zeros_like(mask)
        cv2.fillPoly(ol, [hull], 255)
    else:
        ol = ex.outline_mask(mask, close_m or 2.0, res)
        k = int(0.8 * res) | 1
        ol = cv2.morphologyEx(ol, cv2.MORPH_OPEN, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (k, k)))
    outs = ex.mask_polygons(ol, fr, eps_m=0.2, min_area_m2=20)
    if not outs:
        cuem.set_status(c, "rejected", ["no outline"])
        return c
    outline = max(outs, key=lambda p: p.area)
    ox, oz = _origin_and_shift(outline)
    ox_pt, oz_pt = clip[0] + ox / m, clip[1] + oz / m  # the origin as a page point
    if outline_only:  # the scale search only needs the envelope
        c["levels"] = [dict(name="outline", outline=[[round(x - ox, 3), round(z - oz, 3)] for x, z in list(outline.exterior.coords)[:-1]],
                            origin_pt=[round(ox_pt, 3), round(oz_pt, 3)], rooms=[], openings=[], extra={})]
        return c
    markers, _ = ex.segment_rooms(mask, ol, fr)
    markers = ex.grow_into_walls(markers, mask, int(0.6 * res))
    ids = [int(i) for i in np.unique(markers) if i > 1]
    polys = ex.room_polygons(markers, fr, ids)
    polys = ex.simplify_coverage(ex.resolve_overlaps(polys))
    free = ((mask == 0) & (ol > 0)).astype(np.uint8)
    doors = ex.doors_between({i: p for i, p in polys.items()}, markers, free, fr)
    entrances = ex.entrances(polys, markers, mask, ol, fr)
    labels = ex.label_rooms(polys, pg.words, fr, m)

    def sh(p):  # clip metres -> plan metres
        return [[round(x - ox, 3), round(z - oz, 3)] for x, z in p]

    rooms = []
    id_of = {}
    for i, p in polys.items():
        q = p
        lab = labels.get(i)
        typ = ex.room_type_of(lab) if lab else None
        id_of[i] = len(rooms)
        rooms.append(dict(label=lab, type=typ, poly=sh(list(q.exterior.coords)[:-1]), confidence=0.6 if lab else 0.4, area_m2=round(q.area, 1), _poly=q))
    openings = []
    for i, j, a, b, w in doors:
        if i in id_of and j in id_of:
            openings.append(dict(kind="door", between=[id_of[i], id_of[j]], a=[round(a[0] - ox, 3), round(a[1] - oz, 3)],
                                 b=[round(b[0] - ox, 3), round(b[1] - oz, 3)], width_m=round(w, 2), source="free-space contact", confidence=0.5))
    for i, a, b, w in entrances:
        if i in id_of:
            openings.append(dict(kind="door", between=[id_of[i], None], a=[round(a[0] - ox, 3), round(a[1] - oz, 3)],
                                 b=[round(b[0] - ox, 3), round(b[1] - oz, 3)], width_m=round(w, 2), source="gap in the outer wall", confidence=0.4))
    level = dict(name="Erdgeschoss" if True else "", outline=sh(list(outline.exterior.coords)[:-1]),
                 walls=dict(widths_pt=classes["wall_widths"], heaviest_pt=classes["heaviest"], n_strokes=len(strokes), n_solids=len(solids),
                            table=classes["table"], note="walls are rasterised, see the debug mask; rooms hold the usable geometry"),
                 rooms=[{k: v for k, v in r.items() if not k.startswith("_")} for r in rooms], openings=openings,
                 origin_pt=[round(ox_pt, 3), round(oz_pt, 3)], extra={})
    c["levels"] = [level]
    na = northarrow.detect(pg.drawings)
    if na:
        level["extra"]["north_arrow"] = dict(dx=na[0], dz=na[1], method=na[2])
        cuem.add_fact(c, "north_arrow_page_dir", [round(na[0], 3), round(na[1], 3)], None, 0, 0.8)
    if stadium:
        sf = stadium_facts(pg, c, m)
        if sf:
            def tp(x, y):  # page point -> plan metres
                return [round((x - ox_pt) * m, 3), round((y - oz_pt) * m, 3)]
            ex_ = level["extra"]
            ex_["pitch_rect"] = [tp(sf["pitch_rect_pt"][0], sf["pitch_rect_pt"][1]), tp(sf["pitch_rect_pt"][2], sf["pitch_rect_pt"][3])]
            cuem.add_fact(c, "pitch_length_m", round(sf["pitch_length_m"], 2), "m", 0, 0.9)
            cuem.add_fact(c, "pitch_width_m", round(sf["pitch_width_m"], 2), "m", 0, 0.9)
            if "seating_front_pt" in sf:
                s = sf["seating_front_pt"]
                r_ = sf["roof_outer_pt"]
                ex_["seating_front"] = [tp(s[0], s[1]), tp(s[2], s[3])]
                ex_["roof_outer"] = [tp(r_[0], r_[1]), tp(r_[2], r_[3])]
                pc = sf["pitch_centre_pt"]
                ex_["pitch_centre"] = tp(pc[0], pc[1])
                ex_["pitch_horizontal"] = sf["pitch_horizontal"]
                cuem.add_fact(c, "seating_front_size_m", [round((s[2] - s[0]) * m, 2), round((s[3] - s[1]) * m, 2)], "m", 0, 0.8)
                cuem.add_fact(c, "roof_outer_size_m", [round((r_[2] - r_[0]) * m, 2), round((r_[3] - r_[1]) * m, 2)], "m", 0, 0.8)
    arrows = ex.red_arrows(sel, clip)
    near = []
    ring = outline.exterior
    from shapely.geometry import Point
    for tip, v in arrows:
        q = Point((tip[0] - clip[0]) * m, (tip[1] - clip[1]) * m)
        if ring.distance(q) <= 6.0:
            near.append(dict(tip=[round(q.x - ox, 3), round(q.y - oz, 3)], dir=[round(v[0], 3), round(v[1], 3)]))
    level["extra"]["entrance_arrows"] = near
    cuem.add_fact(c, "entrance_arrows_at_outline", len(near), None, 0, 0.5)
    cuem.add_fact(c, "outline_area_m2", round(outline.area, 1), "m2", 0, 0.7)
    cuem.add_fact(c, "n_rooms", len(rooms), None, 0, 0.6)
    if debug_dir:
        os.makedirs(debug_dir, exist_ok=True)
        img = cv2.cvtColor(255 - mask, cv2.COLOR_GRAY2BGR)
        rng = np.random.RandomState(3)
        for i, p in polys.items():
            col = tuple(int(v) for v in rng.randint(80, 235, 3))
            pts = np.array([(x * res, z * res) for x, z in p.exterior.coords], np.int32)
            cv2.fillPoly(img, [pts], col)
            cv2.polylines(img, [pts], True, (0, 0, 0), 1)
        for (_, _, a, b, _w) in doors:
            cv2.line(img, (int(a[0] * res), int(a[1] * res)), (int(b[0] * res), int(b[1] * res)), (0, 0, 255), 3)
        cv2.polylines(img, [np.array([(x * res, z * res) for x, z in outline.exterior.coords], np.int32)], True, (255, 0, 0), 2)
        pdfio.save_png(os.path.join(debug_dir, "rooms.png"), img[:, :, ::-1])
    return c


# ------------------------------------------------------------------------------------------------ place
def _level_outline(c):
    return Polygon(c["levels"][0]["outline"])


def _placement(c, e0, n0, yaw, rms, cov, tile, extra=None):
    d = dict(e=round(e0, 3), n=round(n0, 3), yaw_deg=round(yaw % 360, 3), fit_rms_m=round(rms, 3), coverage=round(cov, 4), tile=tile)
    if extra:
        d.update(extra)
    c["placement"] = d


def place_stage(c, chunks, cache_dir, info, egid=None, log=print):
    if not c["levels"]:
        cuem.set_status(c, "rejected", ["nothing extracted to place"])
        return c
    lvl = c["levels"][0]
    at = info.get("at")
    if not at:
        cuem.set_status(c, "rejected", ["no map position hint for this venue (--at E,N)"])
        return c
    arrow = lvl["extra"].get("north_arrow")
    yaw_hint = place.yaw_of_north_arrow(arrow["dx"], arrow["dz"]) if arrow else None
    if info["kind"] == "stadium":
        return _place_stadium(c, cache_dir, info, yaw_hint, log)
    b = place.pick_building(chunks, at[0], at[1], egid=egid)
    if b is None:
        cuem.set_status(c, "rejected", ["no building solid near the position hint in the tiles"])
        return c
    outline = _level_outline(c)
    f = place.fit_footprint(outline, b["fp"], yaw_hint=yaw_hint)
    cx, cz = f["origin"]
    e0, n0 = place.to_lv95(-cx, -cz, f["e"], f["n"], f["yaw_deg"])
    tile = "%d_%d" % tiles.tile_of(e0, n0)
    _placement(c, e0, n0, f["yaw_deg"], f["rms_m"], f["coverage"], tile,
               dict(iou=round(f["iou"], 4), egid=int(b["egid"]), footprint_area_m2=round(b["fp"].area, 1),
                    yaw_hint_deg=None if yaw_hint is None else round(yaw_hint, 2), method="footprint_correlation"))
    st, why = place.status_of_fit(f)
    if c["scale"]["method"] != "scale_bar" and not c["scale"].get("cross_check"):
        st = "low_confidence" if st == "ok" else st
    cuem.set_status(c, st, why)
    return c


def _place_stadium(c, cache_dir, info, yaw_hint, log):
    lvl = c["levels"][0]
    e = lvl["extra"]
    at = info["at"]
    if "seating_front" not in e:
        cuem.set_status(c, "rejected", ["no pitch and seating front found in the drawing"])
        return c
    img, box, ppm = aerial.fetch(at[0] - 140, at[1] - 140, at[0] + 140, at[1] + 140, cache_dir)
    (sx0, sz0), (sx1, sz1) = e["seating_front"]
    (px0_, pz0_), (px1_, pz1_) = e["pitch_rect"]
    cxp, czp = (px0_ + px1_) / 2, (pz0_ + pz1_) / 2
    horizontal = e.get("pitch_horizontal", True)
    g = place.grass_rect(img, box, ppm, at[0], at[1])
    if g is None:
        cuem.set_status(c, "rejected", ["no pitch-sized grass area in the aerial near the position hint"])
        return c
    base = g["yaw_deg"] if horizontal else (g["yaw_deg"] + 90) % 180
    cands = [base % 360, (base + 180) % 360]
    note = []
    if yaw_hint is not None:
        d = [abs((x - yaw_hint + 180) % 360 - 180) for x in cands]
        yaw = cands[int(np.argmin(d))]
        if min(d) > 10:
            note.append("north arrow disagrees with the aerial's pitch axis by %.1f deg" % min(d))
    else:
        yaw = cands[0]
        note.append("no north arrow: the two ends of the pitch cannot be told apart")
    # the pitch (a margin of grass round its lines) slid over the aerial's grass-or-shadow mask
    pl = abs(px1_ - px0_) + 2 * GRASS_MARGIN_M
    pw = abs(pz1_ - pz0_) + 2 * GRASS_MARGIN_M
    if not horizontal:
        pl, pw = pw, pl
    f = place.fit_rect_to_aerial(img, box, ppm, (pl, pw), yaw, (g["e"], g["n"]))
    e0, n0 = place.to_lv95(-cxp, -czp, f["e"], f["n"], f["yaw_deg"])
    yaw = f["yaw_deg"]
    fine = place.refine_pitch_centre(img, box, ppm, *place.to_lv95(cxp, czp, e0, n0, yaw), yaw, width_m=min(abs(px1_ - px0_), abs(pz1_ - pz0_)))
    shift = None
    if fine:
        # the pitch's own markings (centre circle, halfway line) beat the shadow-biased template
        shift = math.hypot(fine["e"] - f["e"], fine["n"] - f["n"])
        f["e"], f["n"], yaw = fine["e"], fine["n"], fine["yaw_deg"]
        e0, n0 = place.to_lv95(-cxp, -czp, f["e"], f["n"], yaw)
    else:
        note.append("centre circle not found in the aerial: placement from the grass template only")
    # how well the placed pitch (with its margin) matches the mask
    rect = Polygon([(cxp - pl / 2, czp - pw / 2), (cxp + pl / 2, czp - pw / 2), (cxp + pl / 2, czp + pw / 2), (cxp - pl / 2, czp + pw / 2)])
    placed = Polygon([place.to_lv95(x, z, e0, n0, yaw) for x, z in rect.exterior.coords])
    gm = f["mask"].astype(np.uint8)
    edge = cv2.morphologyEx(gm, cv2.MORPH_GRADIENT, np.ones((3, 3), np.uint8))
    dt = cv2.distanceTransform(1 - edge, cv2.DIST_L2, 5)
    ring = placed.exterior
    ds = []
    for t in np.linspace(0, ring.length, int(ring.length)):
        p = ring.interpolate(t)
        x, y = aerial.lv95_to_px(p.x, p.y, box, ppm)
        ds.append(dt[min(dt.shape[0] - 1, max(0, int(y)))][min(dt.shape[1] - 1, max(0, int(x)))] / ppm)
    rms = float(np.sqrt(np.mean(np.square(ds))))
    iou = f["iou"]
    tile = "%d_%d" % tiles.tile_of(e0, n0)
    _placement(c, e0, n0, yaw, rms, f["inside"], tile,
               dict(iou=round(iou, 4), method="aerial_centre_circle" if fine else "aerial_pitch_template",
                    refine_shift_m=None if shift is None else round(shift, 2), yaw_hint_deg=None if yaw_hint is None else round(yaw_hint, 2),
                    grass_blob_size_m=[round(g["length"], 1), round(g["width"], 1)], pitch_template_m=[round(pl, 1), round(pw, 1)]))
    # the roof's outer edge as drawn, against the nearest visible edge of the aerial along its normals
    (rx0, rz0), (rx1, rz1) = e["roof_outer"]
    roof = [place.to_lv95(x, z, e0, n0, yaw) for x, z in ((rx0, rz0), (rx1, rz0), (rx1, rz1), (rx0, rz1), (rx0, rz0))]
    eo = place.outline_edge_offsets(img, box, ppm, roof)
    if eo:
        c["placement"]["roof_edge_offset_m"] = dict(median=round(eo[0], 2), p90=round(eo[1], 2), rms=round(eo[3], 2), samples=eo[2])
        c["placement"]["fit_rms_m"] = round(eo[3], 3)  # a stadium's fit is judged on its roof outline against the aerial
        rms = eo[3]
    st = "ok"
    why = list(note)
    if eo and eo[0] > 2.0:
        why.append("the roof outline is a median %.1f m from the nearest edge in the aerial" % eo[0])
        st = "low_confidence"
    if rms > 2.0 or iou < 0.8:
        st = "low_confidence"
        why.append("placed seating front is %.1f m (rms) off the grass edge, IoU %.2f" % (rms, iou))
    if note:
        st = "low_confidence"
    cuem.set_status(c, st, why)
    return c


# ------------------------------------------------------------------------------------------------ raster pages
def raster_stage(root, venue, rel_path, page, chunks, info, region=None, width_px=1400, debug_dir=None):
    """A scan: outline only, scale from the footprint (circular), so low confidence at best, rejected on a poor fit."""
    import raster
    doc = pdfio.open_doc(os.path.join(root, rel_path))
    pg = doc[page - 1]
    clip = pymupdf_rect(region) if region else None
    img = pdfio.render(pg, width_px=width_px, clip=clip, gray=True)
    sc = width_px / (clip.width if clip else pg.rect.width)  # px per page point
    c = cuem.new_cue(venue, rel_path, page, region or (0, 0, pg.rect.width, pg.rect.height), "raster")
    poly, ink = raster.outline_px(img)
    if poly is None or poly.is_empty:
        cuem.set_status(c, "rejected", ["no outline found in the scan"])
        return c
    at = info.get("at")
    b = place.pick_building(chunks, at[0], at[1], radius=80) if at else None
    if b is None:
        cuem.set_status(c, "rejected", ["no building solid near the position hint to fit the scan to"])
        return c
    r, s = place.scale_search(poly, b["fp"])
    m_per_pt = s * sc  # metres per page point
    sp = poly_scaled(poly, s)
    cx, cz = r["origin"]
    e0, n0 = place.to_lv95(-cx, -cz, r["e"], r["n"], r["yaw_deg"])
    shifted = [[round(x - cx, 3), round(z - cz, 3)] for x, z in list(sp.exterior.coords)[:-1]]
    c["scale"] = dict(m_per_pt=m_per_pt, method="footprint_fit", cross_check=None, m_per_px=s)
    ox_pt = (clip.x0 if clip else 0) + cx / s / sc
    oz_pt = (clip.y0 if clip else 0) + cz / s / sc
    c["levels"] = [dict(name="scan", outline=shifted, walls=dict(note="raster: outline only"), rooms=[], openings=[],
                        origin_pt=[ox_pt, oz_pt], extra={})]
    cuem.add_fact(c, "outline_area_m2", round(sp.area, 1), "m2", 0, 0.3)
    c["placement"] = dict(e=round(e0, 3), n=round(n0, 3), yaw_deg=round(r["yaw_deg"], 3), fit_rms_m=round(r["rms_m"], 3), coverage=round(r["coverage"], 4),
                          tile="%d_%d" % tiles.tile_of(e0, n0), iou=round(r["iou"], 4), egid=int(b["egid"]), method="footprint_correlation_with_scale_search",
                          footprint_area_m2=round(b["fp"].area, 1))
    why = ["scale taken from the footprint fit itself (no readable scale bar): circular, cannot confirm the plan's size"]
    st, w2 = place.status_of_fit(r)
    if st == "ok":
        st = "low_confidence"
    cuem.set_status(c, st, why + w2)
    if debug_dir:
        os.makedirs(debug_dir, exist_ok=True)
        pdfio.save_png(os.path.join(debug_dir, "ink.png"), ink)
    return c


def pymupdf_rect(box):
    import pymupdf
    return pymupdf.Rect(*box)


def poly_scaled(poly, s):
    return place.scaled(poly, s)

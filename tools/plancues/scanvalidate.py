"""Does the scan room extractor (scanrooms.py) work? Vector pages whose rooms the vector extraction found are the ground truth: each is rendered at
150 and 300 dpi, the raster extractor runs on the render at the known scale (m per point * 72 / dpi), and its rooms and doors are compared with the
vector ones. Pages are split by document: the parameters are set on the training documents, the numbers that count are the held-out ones.

Rooms match one to one at IoU >= 0.5 (greedy, best pair first). Recall = matched / truth rooms, precision = matched / extracted rooms; truth rooms are
those over the game's limits (area and thickness). A door matches when its middle lies within DOOR_TOL_M of a truth door's middle.
"""
import csv
import math
import os
import statistics
import sys

import numpy as np
from shapely.geometry import Polygon
from shapely.strtree import STRtree

import cue as cuem
import pdfio
import pipeline
import raster
import scanrooms

ROOT, OUT = pdfio.DEFAULT_ROOT, pdfio.DEFAULT_OUT
DOOR_TOL_M = 1.0
IOU_MIN = 0.5

# (id, venue, cue name or None, path, page, region, m_per_pt or None (the cue's), split)
PAGES = [
    ("UniS p2", "UniBE_UniS_GrosseSchanze", "UniS_floor_plans_p2", "train"),
    ("UniS p3 r2", "UniBE_UniS_GrosseSchanze", "UniS_floor_plans_p3_r2", "train"),
    ("UniS p3 r3", "UniBE_UniS_GrosseSchanze", "UniS_floor_plans_p3_r3", "train"),
    ("UniS p4 r2", "UniBE_UniS_GrosseSchanze", "UniS_floor_plans_p4_r2", "train"),
    ("ZPK auditorium p1", "Bern_Zentrum_Paul_Klee", "2022_2_auditorium_grundriss_masse_p1", "train"),
    ("Synathlon p6", "UNIL_Synathlon", "Synathlon_brochure_plans_p6", "test"),
    ("Synathlon p8 r1", "UNIL_Synathlon", "Synathlon_brochure_plans_p8_r1", "test"),
    ("Synathlon p9 r1", "UNIL_Synathlon", "Synathlon_brochure_plans_p9_r1", "test"),
    ("Synathlon p11 r1", "UNIL_Synathlon", "Synathlon_brochure_plans_p11_r1", "test"),
    ("vonRoll p24", "UniBE_vonRoll", "vonRoll_brochure_plans_sections_p24", "test"),
    ("vonRoll p25", "UniBE_vonRoll", "vonRoll_brochure_plans_sections_p25", "test"),
    ("vonRoll p27", "UniBE_vonRoll", "vonRoll_brochure_plans_sections_p27", "test"),
    ("BASPO Bauwelt p3 r2", "Magglingen_BASPO", "BASPO_Bauwelt_plans_sections_p3_r2", "test"),
    ("BASPO Bauwelt p3 r4", "Magglingen_BASPO", "BASPO_Bauwelt_plans_sections_p3_r4", "test"),
]
LUCERNE = ("Lucerne Bauwelt p4 r1 (1:1000)", "UniLuzern", "Universities/UniLuzern/Bauwelt_2012_33_plans.pdf", 4, (80.0, 60.0, 480.0, 295.0), 0.3528, "test")


def _limits_ok(p):
    r = list(p.minimum_rotated_rectangle.exterior.coords)
    return p.area >= scanrooms.MIN_AREA_M2 and min(math.dist(r[0], r[1]), math.dist(r[1], r[2])) >= scanrooms.MIN_THICK_M


def truth_of(c):
    """(rooms as shapely polygons in metres from the clip's corner, doors [(mid, width)], clip, m_per_pt) of a vector cue."""
    m = c["scale"]["m_per_pt"]
    lvl = c["levels"][0]
    bbox = c["sources"][0]["bbox"]
    ox, oz = (lvl["origin_pt"][0] - bbox[0]) * m, (lvl["origin_pt"][1] - bbox[1]) * m  # the cue origin from the clip's corner, in metres
    rooms = []
    for r in lvl["rooms"]:
        if not r["poly"]:
            continue
        p = Polygon([(x + ox, z + oz) for x, z in r["poly"]]).buffer(0)
        if p.geom_type == "Polygon" and not p.is_empty:
            rooms.append(p)
    doors = []
    for o in lvl["openings"]:
        if o.get("between") and None not in o["between"] and not str(o.get("source", "")).startswith("inferred"):
            doors.append((((o["a"][0] + o["b"][0]) / 2 + ox, (o["a"][1] + o["b"][1]) / 2 + oz), o["width_m"]))
    return rooms, doors, tuple(bbox), m


def match_rooms(truth, got):
    """Greedy one to one matching at IoU >= IOU_MIN: list of (truth index, got index, iou)."""
    if not truth or not got:
        return []
    tree = STRtree(truth)
    pairs = []
    for j, g in enumerate(got):
        for i in tree.query(g):
            inter = truth[i].intersection(g).area
            if inter <= 0:
                continue
            iou = inter / (truth[i].area + g.area - inter)
            if iou >= IOU_MIN:
                pairs.append((iou, int(i), j))
    pairs.sort(reverse=True)
    ti, gj, out = set(), set(), []
    for iou, i, j in pairs:
        if i not in ti and j not in gj:
            ti.add(i)
            gj.add(j)
            out.append((i, j, iou))
    return out


def match_doors(truth, got, tol=DOOR_TOL_M):
    pairs = sorted((math.dist(t[0], g), i, j) for i, t in enumerate(truth) for j, g in enumerate(got) if math.dist(t[0], g) <= tol)
    ti, gj, n = set(), set(), 0
    for d, i, j in pairs:
        if i not in ti and j not in gj:
            ti.add(i)
            gj.add(j)
            n += 1
    return n


def render_clip(path, page, clip, dpi):
    import pymupdf
    doc = pdfio.open_doc(os.path.join(ROOT, path))
    pg = doc[page - 1]
    r = pymupdf.Rect(*clip)
    return pdfio.render(pg, width_px=r.width / 72.0 * dpi, clip=r, gray=True)


def evaluate(truth_rooms, truth_doors, source_path, page, clip, m_per_pt, dpi):
    """Metrics of one page at one resolution."""
    gray = render_clip(source_path, page, clip, dpi)
    m_px = m_per_pt * 72.0 / dpi
    poly_px, _ = raster.outline_px(gray)
    outline = None if poly_px is None else Polygon([(x * m_px, y * m_px) for x, y in poly_px.exterior.coords])
    res = scanrooms.extract(gray, m_px, outline=outline)
    tr = [p for p in truth_rooms if _limits_ok(p)]
    got = res["rooms"]
    mt = match_rooms(tr, got)
    gd = [(((d["a"][0] + d["b"][0]) / 2, (d["a"][1] + d["b"][1]) / 2)) for d in res["doors"]]
    nd = match_doors(truth_doors, gd)
    ious = [m[2] for m in mt]
    return dict(dpi=dpi, ok=res["ok"], why=res["why"], wall_m=res["wall_m"], truth_rooms=len(tr), got_rooms=len(got), matched=len(mt),
                recall=len(mt) / len(tr) if tr else None, precision=len(mt) / len(got) if got else (0.0 if tr else None),
                median_iou=statistics.median(ious) if ious else None,
                truth_doors=len(truth_doors), got_doors=len(gd), door_matched=nd,
                door_recall=nd / len(truth_doors) if truth_doors else None, door_precision=nd / len(gd) if gd else None)


def load_pages(out=OUT):
    pages = []
    for pid, venue, name, split in PAGES:
        c = cuem.load(cuem.cue_path(out, venue, name))
        rooms, doors, clip, m = truth_of(c)
        pages.append(dict(id=pid, split=split, rooms=rooms, doors=doors, clip=clip, m=m, path=c["sources"][0]["path"], page=c["sources"][0]["page"]))
    pid, venue, path, page, region, m, split = LUCERNE
    c = cuem.new_cue(venue, path, page, region, "vector")
    c["scale"] = dict(m_per_pt=m, method="written ratio 1:1000", cross_check=None)
    pg = pipeline.Page(ROOT, path, page)
    pipeline.extract_stage(c, pg, "building")
    if c["levels"]:
        rooms, doors, clip, m = truth_of(c)
        pages.append(dict(id=pid, split=split, rooms=rooms, doors=doors, clip=clip, m=m, path=path, page=page))
    return pages


def run(pages, dpis=(150, 300), log=print):
    rows = []
    for p in pages:
        for dpi in dpis:
            try:
                r = evaluate(p["rooms"], p["doors"], p["path"], p["page"], p["clip"], p["m"], dpi)
            except Exception as e:  # a page that cannot be rendered or extracted is a miss, not a crash
                r = dict(dpi=dpi, ok=False, why="error: %r" % (e,), truth_rooms=len([q for q in p["rooms"] if _limits_ok(q)]), got_rooms=0, matched=0, recall=0.0,
                         precision=None, median_iou=None, truth_doors=len(p["doors"]), got_doors=0, door_matched=0)
            r.update(page=p["id"], split=p["split"])
            rows.append(r)
            log("%-28s %s %3d dpi rooms %3s/%3s got %3s recall %s prec %s medIoU %s doors %s/%s got %s wall %s %s" % (
                p["id"], p["split"], dpi, r["matched"], r["truth_rooms"], r["got_rooms"], _f(r["recall"]), _f(r["precision"]), _f(r["median_iou"]),
                r["door_matched"], r["truth_doors"], r["got_doors"], r["wall_m"], r["why"] or ""))
    return rows


def _f(x):
    return "  -  " if x is None else "%.2f" % x


def summary(rows, split):
    """Pooled recall and precision (sum of matches over sum of rooms), median of the per-page median IoU, pooled door recall and precision."""
    rs = [r for r in rows if r["split"] == split]
    t, g, m = (sum(r[k] for r in rs) for k in ("truth_rooms", "got_rooms", "matched"))
    td, gd, md = (sum(r[k] or 0 for r in rs) for k in ("truth_doors", "got_doors", "door_matched"))
    ious = [r["median_iou"] for r in rs if r["median_iou"] is not None]
    return dict(split=split, pages=len(rs), truth_rooms=t, got_rooms=g, matched=m, recall=m / t if t else None, precision=m / g if g else None,
                median_iou=statistics.median(ious) if ious else None, truth_doors=td, got_doors=gd, door_matched=md,
                door_recall=md / td if td else None, door_precision=md / gd if gd else None,
                median_page_recall=statistics.median([r["recall"] for r in rs if r["recall"] is not None]) if rs else None)


def write_csv(rows, path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    keys = ["page", "split", "dpi", "truth_rooms", "got_rooms", "matched", "recall", "precision", "median_iou", "truth_doors", "got_doors", "door_matched",
            "door_recall", "door_precision", "wall_m", "ok", "why"]
    with open(path, "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, keys, extrasaction="ignore")
        w.writeheader()
        for r in rows:
            w.writerow(r)


def tune(pages, grid, dpis=(150, 300)):
    """Score each parameter set on the training pages only. grid: list of dicts of scanrooms module constants."""
    train = [p for p in pages if p["split"] == "train"]
    saved = {k: getattr(scanrooms, k) for g in grid for k in g}
    out = []
    for g in grid:
        for k, v in g.items():
            setattr(scanrooms, k, v)
        rows = run(train, dpis, log=lambda *a: None)
        s = summary(rows, "train")
        out.append((g, s))
        print(g, "recall %s precision %s medIoU %s doors %s/%s got %s" % (_f(s["recall"]), _f(s["precision"]), _f(s["median_iou"]), s["door_matched"], s["truth_doors"], s["got_doors"]), flush=True)
    for k, v in saved.items():
        setattr(scanrooms, k, v)
    return out


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    pages = load_pages()
    if "--tune" in sys.argv:
        grid = [dict(DOOR_M=d, OPEN_FACTOR=o) for d in (0.9, 1.2) for o in (0.5, 0.7, 0.9)]
        tune(pages, grid)
    else:
        rows = run(pages)
        write_csv(rows, os.path.join(OUT, "_all", "scan_validation.csv"))
        for sp in ("train", "test"):
            print(summary(rows, sp))

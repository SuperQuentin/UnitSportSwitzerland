"""Walls, rooms and doors from a raster plan (a scan) whose scale in metres per pixel is known. Measured, not guessed; no names (there is no OCR).

1. Ink: the drawing's dark marks against its background (background = a wide grey dilation of the page, so poché and pale scans both work).
2. Wall width: a histogram of the stroke widths of the ink (distance transform ridge, 2 * d - 0.5). The wall is the heaviest width class that carries at
   least WALL_SHARE of the ridge pixels thicker than a hairline; strokes thinner than 0.7 of it (text, hatching, dimension lines, furniture) are
   removed by an opening, and pieces under 1.2 m long are dropped.
3. Rooms: the free spaces inside the outline. The walls are first closed with a line of DOOR_M (the width of a doorway) at four orientations, so a doorway
   in a wall is bridged and the two rooms come apart (a disk does not bridge a gap between thin wall ends: it fits against the opening); each room is
   then grown back through the free space (the reopening) and half way into the walls.
4. Doors: a connected piece of what the closing added that touches exactly two rooms is a door (`source: "scan gap"`); its width is the long side of
   its minimum rectangle, between DOOR_MIN_M and DOOR_MAX_M.
Rooms under the game's limits (area `export.MIN_AREA`, thickness `export.MIN_THICK`) are dropped, as the export would.
"""
import math
from types import SimpleNamespace

import cv2
import numpy as np
from shapely.geometry import Point, Polygon

import extract as ex

WORK_PX_PER_M = 20.0  # finer sources are shrunk to this, coarser ones enlarged (at most 3x) so the morphology has pixels to work with
DOOR_M = 1.2
DOOR_MIN_M, DOOR_MAX_M = 0.5, 2.4
MIN_WALL_M, MAX_WALL_M = 0.07, 0.9
WALL_SHARE = 0.15
MIN_PIECE_M = 1.2
MIN_AREA_M2, MIN_THICK_M = 1.0, 1.0  # export.MIN_AREA, export.MIN_THICK


def _disk(d):
    d = max(1, int(d)) | 1
    return cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (d, d))


def ink_of(gray, ppm):
    """Dark marks of a grey page as a 0/255 mask. The threshold is Otsu over the marks only, so grey floor fills fall below it and black ink stays."""
    g = cv2.GaussianBlur(gray, (0, 0), 0.8)
    k = max(15, int(0.03 * max(g.shape)), int(1.5 * ppm)) | 1
    bg = cv2.GaussianBlur(cv2.dilate(g, _disk(k)), (0, 0), k / 3.0)
    diff = np.clip(bg.astype(np.int16) - g.astype(np.int16), 0, 255).astype(np.uint8)
    marks = diff[diff > 20]
    if marks.size < 50:
        return np.zeros(gray.shape, np.uint8)
    t, _ = cv2.threshold(marks.reshape(-1, 1), 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
    t = max(20.0, min(t, 0.6 * float(np.percentile(marks, 99))))
    return (diff > t).astype(np.uint8) * 255


def stroke_widths(ink):
    """Widths in pixels of the ink strokes, one value per ridge pixel of the distance transform."""
    dt = cv2.distanceTransform(ink, cv2.DIST_L2, 5)
    ridge = (dt >= cv2.dilate(dt, np.ones((3, 3), np.uint8))) & (dt >= 1.0)
    return 2.0 * dt[ridge] - 0.5


def wall_width(ink, ppm):
    """(width in px, share of the thick ridge pixels it carries) of the walls, or (None, 0) when no stroke class qualifies."""
    w = stroke_widths(ink)
    lo, hi = max(2.0, MIN_WALL_M * ppm), MAX_WALL_M * ppm
    w = w[(w >= lo * 0.8) & (w <= hi * 2.0)]
    if w.size < 30:
        return None, 0.0
    best = None
    for c in np.arange(hi, lo - 1e-6, -0.5):
        share = float(((w >= 0.75 * c) & (w <= 1.33 * c)).mean())
        if share >= WALL_SHARE:
            best = (c, share)
            break
    if best is None:
        return None, 0.0
    c, share = best
    sel = w[(w >= 0.75 * c) & (w <= 1.33 * c)]
    return float(np.median(sel)), share


def walls_of(ink, w, ppm, inside=None):
    """Strokes about as heavy as the walls: the opening removes what is thinner than 0.7 w, pieces under MIN_PIECE_M and anything outside the outline go."""
    walls = cv2.morphologyEx(ink, cv2.MORPH_OPEN, _disk(max(2, round(0.7 * w))))
    if inside is not None:
        walls = cv2.bitwise_and(walls, cv2.dilate(inside, _disk(w + 2)))
    n, lab, st, _ = cv2.connectedComponentsWithStats(walls, connectivity=8)
    keep = np.zeros(n, bool)
    for i in range(1, n):
        if max(st[i, cv2.CC_STAT_WIDTH], st[i, cv2.CC_STAT_HEIGHT]) >= MIN_PIECE_M * ppm:
            keep[i] = True
    return np.where(keep[lab], 255, 0).astype(np.uint8)


def line_kernel(d, deg):
    k = np.zeros((d, d), np.uint8)
    c = (d - 1) / 2.0
    dx, dy = math.cos(math.radians(deg)) * c, math.sin(math.radians(deg)) * c
    cv2.line(k, (int(round(c - dx)), int(round(c - dy))), (int(round(c + dx)), int(round(c + dy))), 1, 1)
    return k


def close_gaps(walls, d):
    """Walls closed with a line of d pixels at 0, 45, 90 and 135 degrees: a gap in a wall shorter than d is bridged, whichever way the wall runs."""
    out = walls.copy()
    for deg in (0, 45, 90, 135):
        out = cv2.bitwise_or(out, cv2.morphologyEx(walls, cv2.MORPH_CLOSE, line_kernel(d, deg)))
    return out


def _grow(lab, allowed, steps):
    """Labels grown into the pixels where `allowed` is set, a pixel at a time (ties go to the larger label)."""
    k = np.ones((3, 3), np.uint8)
    for _ in range(steps):
        d = cv2.dilate(lab.astype(np.float32), k).astype(np.int32)
        take = (lab == 0) & (d > 0) & allowed
        if not take.any():
            break
        lab[take] = d[take]
    return lab


def extract(gray, m_per_px, outline=None, door_m=DOOR_M, wall_m=None):
    """Rooms and doors of a grey raster plan.

    `outline` is a shapely polygon in metres from the raster's top-left corner (x right, z down), else the plan's own outline is traced.
    Returns dict(ok, why, wall_m, wall_share, rooms=[Polygon], doors=[dict(between, a, b, width_m)], dropped, work_px_per_m). Coordinates are metres in
    the raster's frame, the same as `outline`.
    """
    h0, w0 = gray.shape
    ppm0 = 1.0 / m_per_px
    f = min(3.0, WORK_PX_PER_M / ppm0)
    if abs(f - 1.0) > 0.05:
        gray = cv2.resize(gray, None, fx=f, fy=f, interpolation=cv2.INTER_AREA if f < 1 else cv2.INTER_CUBIC)
    ppm = ppm0 * f
    res = dict(ok=False, why=None, wall_m=None, wall_share=0.0, rooms=[], doors=[], dropped=[], work_px_per_m=round(ppm, 2))
    ink = ink_of(gray, ppm)
    if outline is None:
        import raster
        poly_px, _ = raster.outline_px(gray)
        if poly_px is None:
            res["why"] = "no outline found in the scan"
            return res
        outline = Polygon([(x / ppm, y / ppm) for x, y in poly_px.exterior.coords])
    inside = np.zeros(gray.shape, np.uint8)
    cv2.fillPoly(inside, [np.round(np.array(outline.exterior.coords) * ppm).astype(np.int32)], 255)
    if wall_m is None:
        sel = cv2.bitwise_and(ink, cv2.dilate(inside, _disk(6)))
        w, share = wall_width(sel, ppm)
    else:
        w, share = wall_m * ppm, 1.0
    if w is None:
        res["why"] = "no stroke class qualifies as a wall (none heavy enough, or too faint for this resolution)"
        return res
    res["wall_m"], res["wall_share"] = round(w / ppm, 3), round(share, 3)
    walls = walls_of(ink, w, ppm, inside)
    if not walls.any():
        res["why"] = "no walls left after removing thin strokes"
        return res
    D = max(3, int(round(door_m * ppm))) | 1
    closed = close_gaps(walls, D)
    free_c = ((closed == 0) & (inside > 0)).astype(np.uint8)
    free_c = cv2.morphologyEx(free_c, cv2.MORPH_OPEN, np.ones((3, 3), np.uint8))
    n, lab = cv2.connectedComponents(free_c, connectivity=4)
    lab = lab.astype(np.int32)
    # a doorway: a piece of what the closing added, touching exactly two rooms
    gap = ((closed > 0) & (walls == 0) & (inside > 0)).astype(np.uint8)
    gn, glab = cv2.connectedComponents(gap, connectivity=8)
    doors = []
    near = cv2.dilate(lab.astype(np.float32), np.ones((3, 3), np.uint8)).astype(np.int32)
    for g in range(1, gn):
        m = glab == g
        if m.sum() < 4:
            continue
        # rooms in contact with the gap: their labels in a one pixel ring round it
        ring = cv2.dilate(m.astype(np.uint8), _disk(3)) > 0
        ids, cnt = np.unique(lab[ring & (lab > 0)], return_counts=True)
        ids = [(int(c), int(i)) for i, c in zip(ids, cnt) if c >= 4]
        if len(ids) != 2:
            continue
        pts = np.column_stack(np.nonzero(m))[:, ::-1].astype(np.float32)
        (cx, cy), (rw, rh), ang = cv2.minAreaRect(pts)
        long_m, short_m = max(rw, rh) / ppm, min(rw, rh) / ppm
        if not (DOOR_MIN_M <= long_m <= DOOR_MAX_M) or long_m < 1.3 * short_m:
            continue
        th = math.radians(ang if rw >= rh else ang + 90.0)
        dx, dy = math.cos(th) * max(rw, rh) / 2, math.sin(th) * max(rw, rh) / 2
        doors.append(dict(rooms=(ids[0][1], ids[1][1]), a=((cx - dx) / ppm, (cy - dy) / ppm), b=((cx + dx) / ppm, (cy + dy) / ppm), width_m=round(long_m, 2)))
    # reopen: the rooms take back the free space the closing removed, then reach the middle of the walls
    free = ((walls == 0) & (inside > 0))
    lab = _grow(lab, free, int(D / 2) + 3)
    lab = _grow(lab, inside > 0, int(math.ceil(w / 2)) + 1)
    fr = SimpleNamespace(res=ppm)
    ids = [int(i) for i in np.unique(lab) if i > 0]
    polys = ex.room_polygons(lab, fr, ids, eps_m=0.12, min_area=MIN_AREA_M2)
    polys = ex.simplify_coverage(ex.resolve_overlaps(polys, grow=0.0), 0.12)
    keep, dropped = {}, []
    for i, p in polys.items():
        r_ = list(p.minimum_rotated_rectangle.exterior.coords)
        thick = min(math.dist(r_[0], r_[1]), math.dist(r_[1], r_[2]))
        if p.area < MIN_AREA_M2 or thick < MIN_THICK_M:
            dropped.append(dict(area_m2=round(p.area, 1), thick_m=round(thick, 2)))
            continue
        keep[i] = p
    if sum(p.area for p in keep.values()) > 0.97 * outline.area and len(keep) <= 1:
        res["why"] = "the free space is one piece: the walls do not close any room"
        return res
    order = sorted(keep)
    idx = {i: k for k, i in enumerate(order)}
    res["rooms"] = [keep[i] for i in order]
    res["doors"] = [dict(between=[idx[d["rooms"][0]], idx[d["rooms"][1]]], a=d["a"], b=d["b"], width_m=d["width_m"]) for d in doors
                    if d["rooms"][0] in idx and d["rooms"][1] in idx]
    res["dropped"] = dropped
    res["ok"] = bool(res["rooms"])
    if not res["ok"]:
        res["why"] = "no room left over the game's limits"
    return res

"""Where a plan sits on the map: LV95 position, yaw and how well it fits.

Buildings: the plan's outline is fitted to the footprint of its solid in our .bldg tile by correlating filled masks
over every turn (the north arrow gives the turn hint) and every shift; the same frame as `BuildingOutline`:
LV95 (e, n) = centre + x * (cos a, sin a) + z * (sin a, -cos a), a = yaw counter-clockwise from east.
Stadiums: our tiles have no bowl, so the pitch's grass is cut out of the swissimage aerial and matched to the plan's
seating front (the dashed line round the pitch), with the north arrow choosing between the two ends.
"""
import math

import cv2
import numpy as np
from shapely import affinity
from shapely.geometry import Point, Polygon

import aerial
import tiles

G = 0.5  # m per pixel of the fitting rasters


def to_lv95(x, z, e0, n0, yaw_deg):
    a = math.radians(yaw_deg)
    c, s = math.cos(a), math.sin(a)
    return e0 + x * c + z * s, n0 + x * s - z * c


def to_plan(e, n, e0, n0, yaw_deg):
    a = math.radians(yaw_deg)
    c, s = math.cos(a), math.sin(a)
    de, dn = e - e0, n - n0
    return de * c + dn * s, de * s - dn * c


def yaw_of_north_arrow(dx, dz):
    """Yaw (deg, the BuildingOutline convention) of a plan whose north arrow points along (dx, dz) on the page (z down)."""
    # bearing of page-right is the angle from north clockwise to it; north points along (dx, -dz) in y-up terms
    north_up = math.degrees(math.atan2(-dz, dx))  # math angle of north, y up
    bearing_right = north_up % 360  # clockwise from north to page-right equals north's angle from page-right
    return (90 - bearing_right) % 360


def _fill(poly, to_px, shape, value=1.0):
    img = np.zeros(shape, np.float32)
    S = 4
    polys = [poly] if poly.geom_type == "Polygon" else list(poly.geoms)
    for p in polys:
        ext = np.round(np.array([to_px(x, y) for x, y in p.exterior.coords]) * (1 << S)).astype(np.int32)
        cv2.fillPoly(img, [ext], value, lineType=cv2.LINE_8, shift=S)
        for h in p.interiors:
            hi = np.round(np.array([to_px(x, y) for x, y in h.coords]) * (1 << S)).astype(np.int32)
            cv2.fillPoly(img, [hi], 0.0, lineType=cv2.LINE_8, shift=S)
    return img


def fit_footprint(plan_poly, fp, yaw_hint=None, hint_tol=12.0, step=2.0, g=G):
    """Fit a plan outline (shapely polygon in plan metres, any origin) to a footprint (shapely, LV95 metres).

    Returns dict(e, n, yaw_deg, iou, coverage, rms_m, origin) where (e, n) is where plan point `origin` falls.
    """
    ox, oz = plan_poly.centroid.x, plan_poly.centroid.y
    plan = affinity.translate(plan_poly, -ox, -oz)
    minx, miny, maxx, maxy = fp.bounds
    Rt = int(math.ceil(max(max(abs(v) for v in plan.bounds), 1) * 1.0 / g)) + 2
    pad = Rt
    W = int((maxx - minx) / g) + 2 * pad + 2
    H = int((maxy - miny) / g) + 2 * pad + 2
    F = _fill(fp, lambda e, n: ((e - minx) / g + pad, (maxy - n) / g + pad), (H, W))
    area_f = float(F.sum())

    def tmpl(yaw):
        a = math.radians(yaw)
        c, s = math.cos(a), math.sin(a)
        return _fill(plan, lambda x, z: (Rt + 0.5 + (c * x + s * z) / g, Rt + 0.5 + (-s * x + c * z) / g), (2 * Rt + 1, 2 * Rt + 1))

    def best_at(yaw):
        T = tmpl(yaw)
        res = cv2.matchTemplate(F, T, cv2.TM_CCORR)
        _, mx, _, loc = cv2.minMaxLoc(res)
        return mx, loc

    cands = np.arange(0, 360, step)
    if yaw_hint is not None:
        cands = np.array([(yaw_hint + d) % 360 for d in np.arange(-hint_tol, hint_tol + 1e-6, step / 2)])
    best = None
    for yaw in cands:
        inter, loc = best_at(float(yaw))
        if best is None or inter > best[0]:
            best = (inter, float(yaw), loc)
    for fine in (0.5, 0.1):  # refine the turn
        base = best[1]
        for d in np.arange(-3 * fine, 3 * fine + 1e-6, fine):
            yaw = (base + d) % 360
            inter, loc = best_at(float(yaw))
            if inter > best[0]:
                best = (inter, float(yaw), loc)
    inter, yaw, loc = best
    area_p = float(tmpl(yaw).sum())  # the template's own raster area: the fill rule makes it larger than polygon.area / g^2
    iou = inter / (area_f + area_p - inter)
    e_c = minx + (loc[0] + Rt + 0.5 - pad) * g
    n_c = maxy - (loc[1] + Rt + 0.5 - pad) * g
    # rms of the plan outline's boundary from the footprint's boundary, in metres
    placed = [(to_lv95(x, z, e_c, n_c, yaw)) for x, z in plan.exterior.coords]
    line = fp.boundary
    ds = []
    ring = Polygon(placed).exterior
    for t in np.linspace(0, ring.length, max(50, int(ring.length))):
        ds.append(line.distance(ring.interpolate(t)))
    rms = float(np.sqrt(np.mean(np.square(ds))))
    return dict(e=e_c, n=n_c, yaw_deg=yaw, iou=float(iou), coverage=float(inter / area_p), rms_m=rms,
                origin=(ox, oz), footprint_cover=float(inter / area_f))


def scaled(poly, s):
    """A plan polygon with every coordinate multiplied by s (about the origin)."""
    return affinity.scale(poly, s, s, origin=(0, 0))


COARSE_FACTORS = np.concatenate([np.arange(0.6, 1.5, 0.05), np.arange(1.5, 1.6, 0.05)])


def scale_search(poly, fp, s0=None, refine=False, factors=COARSE_FACTORS):
    """Scale and turn of a plan outline that make it overlap a footprint most (IoU), coarse then fine.

    `poly` is in any unit (pixels, points); the result `s` multiplies it into metres. `s0` is the scale if the outline filled
    the footprint (default: from the areas); the coarse pass tries `factors` x s0 (20 steps of 5 %, every turn), the fine
    pass 1 % steps over +-4 %. With `refine` the best fine step is sharpened by a parabola through its neighbours.
    Returns (fit dict of `fit_footprint`, s).
    """
    s0 = s0 or math.sqrt(fp.area / poly.area)
    best = None
    for f in factors:
        r = fit_footprint(scaled(poly, s0 * f), fp, g=1.0, step=3.0)
        if best is None or r["iou"] > best[0]["iou"]:
            best = (r, s0 * f)
    r, s1 = best
    fine = []
    for f in np.arange(0.96, 1.041, 0.01):
        r2 = fit_footprint(scaled(poly, s1 * f), fp, g=0.5, step=1.0, yaw_hint=r["yaw_deg"], hint_tol=4.0)
        fine.append((s1 * f, r2["iou"]))
        if r2["iou"] > r["iou"]:
            r, best = r2, (r2, s1 * f)
    r, s = best
    if refine and len(fine) >= 3:
        i = max(range(len(fine)), key=lambda k: fine[k][1])
        if 0 < i < len(fine) - 1:
            (xa, ya), (xb, yb), (xc, yc) = fine[i - 1], fine[i], fine[i + 1]
            den = ya - 2 * yb + yc
            if den < -1e-9:
                s_ref = xb + 0.5 * (xc - xa) / 2 * (ya - yc) / den
                if abs(s_ref / s - 1) <= 0.02:
                    r, s = fit_footprint(scaled(poly, s_ref), fp, g=0.5, step=1.0, yaw_hint=r["yaw_deg"], hint_tol=2.0), s_ref
    return r, s


def status_of_fit(f):
    """ok / low_confidence / rejected with the reasons, from the fit's numbers."""
    reasons = []
    if f["iou"] >= 0.8 and f["rms_m"] <= 2.5:
        return "ok", reasons
    if f["iou"] >= 0.8:
        reasons.append("outline is %.1f m off the footprint boundary (rms)" % f["rms_m"])
        return "low_confidence", reasons
    if f["iou"] >= 0.55:
        reasons.append("outline overlaps the footprint only %.0f %% (IoU)" % (100 * f["iou"]))
        return "low_confidence", reasons
    reasons.append("outline does not fit the footprint (IoU %.2f)" % f["iou"])
    return "rejected", reasons


def pick_building(chunks, e, n, egid=None, radius=60):
    """The solid a plan belongs to: by EGID, else the largest footprint within `radius` of (e, n)."""
    bs = tiles.buildings_near(chunks, e, n, radius)
    if egid is not None:
        for b in bs:
            if b["egid"] == egid:
                return b
        return None
    bs = [b for b in bs if b["fp"].area > 30]
    if not bs:
        return None
    pt = Point(e, n)
    inside = [b for b in bs if b["fp"].contains(pt)]
    return max(inside or bs, key=lambda b: b["fp"].area)


# ----------------------------------------------------------------------------------------- stadium: aerial pitch
def grass_rect(img, box, ppm, near_e, near_n, expect=(114.0, 75.0), open_m=12.0):
    """The pitch's grass cut out of the aerial around (near_e, near_n): the min-area rectangle of its blob.

    Returns dict(e, n, yaw_deg, length, width, mask) with yaw of the long side, counter-clockwise from east, [0, 180).
    """
    hsv = cv2.cvtColor(img, cv2.COLOR_RGB2HSV)
    h, s, v = hsv[:, :, 0], hsv[:, :, 1], hsv[:, :, 2]
    mask = ((h >= 33) & (h <= 85) & (s >= 70) & (v >= 25)).astype(np.uint8) * 255
    k = int(open_m * ppm) | 1
    mask = cv2.morphologyEx(mask, cv2.MORPH_OPEN, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (k, k)))
    k2 = int(3 * ppm) | 1
    mask = cv2.morphologyEx(mask, cv2.MORPH_CLOSE, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (k2, k2)))
    n, lab, st, cen = cv2.connectedComponentsWithStats(mask)
    px, py = aerial.lv95_to_px(near_e, near_n, box, ppm)
    best, bd = None, 1e9
    for i in range(1, n):
        m = (lab == i)
        cnts, _ = cv2.findContours(m.astype(np.uint8), cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        c = max(cnts, key=cv2.contourArea)
        (cx, cy), (rw, rh), ang = cv2.minAreaRect(c)
        L, Wd = max(rw, rh) / ppm, min(rw, rh) / ppm
        d = abs(L - expect[0]) / expect[0] + abs(Wd - expect[1]) / expect[1] + math.hypot(cx - px, cy - py) / ppm / 100
        if d < bd and cv2.contourArea(c) / ppm ** 2 > 2500:
            bd, best = d, (i, (cx, cy), (rw, rh), ang, L, Wd)
    if best is None:
        return None
    i, (cx, cy), (rw, rh), ang, L, Wd = best
    # yaw of the long side counter-clockwise from east (image y is down)
    a = ang if rw >= rh else ang + 90
    yaw = (-a) % 180
    e = box[0] + cx / ppm
    nn = box[3] - cy / ppm
    return dict(e=e, n=nn, yaw_deg=yaw, length=L, width=Wd, mask=(lab == i), score=bd)


def fit_rect_to_aerial(img, box, ppm, size_m, yaw_deg, near, search_m=14.0, yaw_tol=3.0):
    """Slide a filled rectangle of size_m (along x: long, across z) over the aerial's grass-or-shadow mask.

    The pitch is partly in the roof's shadow (dark pixels), so the mask is green grass OR very dark; a rectangle the
    size of the pitch plus a margin is shifted (and turned a little) to cover the most of it.
    Returns dict(e, n, yaw_deg, iou, rms_m, mask) with (e, n) the rectangle's centre.
    """
    hsv = cv2.cvtColor(img, cv2.COLOR_RGB2HSV)
    h, s, v = hsv[:, :, 0], hsv[:, :, 1], hsv[:, :, 2]
    grass = ((h >= 33) & (h <= 85) & (s >= 70) & (v >= 25))
    dark = v < 55
    M = (grass | dark).astype(np.float32)
    M = cv2.morphologyEx(M, cv2.MORPH_OPEN, np.ones((int(2 * ppm) | 1,) * 2, np.uint8))
    L, W = size_m
    R = int(math.ceil(max(L, W) * ppm / 2)) + 4
    best = None
    px0, py0 = aerial.lv95_to_px(near[0], near[1], box, ppm)
    r = int(search_m * ppm)
    x0, x1 = int(px0 - r - R), int(px0 + r + R)
    y0, y1 = int(py0 - r - R), int(py0 + r + R)
    x0, y0 = max(0, x0), max(0, y0)
    sub = M[y0:y1, x0:x1]
    for yaw in np.arange(yaw_deg - yaw_tol, yaw_deg + yaw_tol + 1e-6, 0.5):
        a = math.radians(yaw)
        c, s_ = math.cos(a), math.sin(a)
        T = np.zeros((2 * R + 1, 2 * R + 1), np.float32)
        pts = []
        for x, z in ((-L / 2, -W / 2), (L / 2, -W / 2), (L / 2, W / 2), (-L / 2, W / 2)):
            du, dv = (c * x + s_ * z) * ppm, (-s_ * x + c * z) * ppm
            pts.append((R + 0.5 + du, R + 0.5 + dv))
        cv2.fillPoly(T, [np.round(np.array(pts) * 16).astype(np.int32)], 1.0, lineType=cv2.LINE_8, shift=4)
        res = cv2.matchTemplate(sub, T, cv2.TM_CCORR)
        _, mx, _, loc = cv2.minMaxLoc(res)
        if best is None or mx > best[0]:
            best = (mx, yaw, loc, float(T.sum()), T)
    mx, yaw, loc, area_t, T = best
    cx = x0 + loc[0] + R + 0.5
    cy = y0 + loc[1] + R + 0.5
    e = box[0] + cx / ppm
    n = box[3] - cy / ppm
    win = M[int(cy) - R:int(cy) + R + 1, int(cx) - R:int(cx) + R + 1]
    iou = float((win * T).sum() / (win.sum() + T.sum() - (win * T).sum() + 1e-9)) if win.shape == T.shape else 0.0
    inside_frac = float((win * T).sum() / T.sum()) if win.shape == T.shape else 0.0
    return dict(e=e, n=n, yaw_deg=yaw, iou=iou, inside=inside_frac, mask=M > 0)


def refine_pitch_centre(img, box, ppm, e, n, yaw_deg, width_m=68.0, search_m=7.0, yaw_tol=3.0):
    """Sharpen a pitch placement on its own markings: the centre circle (radius 9.15 m) and the halfway line.

    The line markings are the brightest thin features on the grass: a top-hat of the grey image. The ring's
    correlation over the window gives the centre; the mean along the halfway line, over small turns, gives the yaw.
    Returns dict(e, n, yaw_deg, ring_score, line_score) or None when the ring is not clearly there.
    """
    gray = cv2.cvtColor(img, cv2.COLOR_RGB2GRAY).astype(np.float32)
    k = int(1.2 * ppm) * 2 + 1
    tophat = cv2.morphologyEx(gray, cv2.MORPH_TOPHAT, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (k, k)))
    tophat = cv2.GaussianBlur(tophat, (0, 0), 0.8)
    r = 9.15 * ppm
    size = int(2 * (r + 3)) | 1
    ring = np.zeros((size, size), np.float32)
    cv2.circle(ring, (size // 2, size // 2), int(round(r)), 1.0, 2, cv2.LINE_AA)
    ring /= ring.sum()
    px, py = aerial.lv95_to_px(e, n, box, ppm)
    R = int(search_m * ppm) + size // 2 + 2
    x0, y0 = int(px) - R, int(py) - R
    sub = tophat[max(0, y0):y0 + 2 * R + 1, max(0, x0):x0 + 2 * R + 1]
    res = cv2.matchTemplate(sub, ring, cv2.TM_CCORR)
    _, mx, _, loc = cv2.minMaxLoc(res)
    cx = max(0, x0) + loc[0] + size // 2 + 0.5
    cy = max(0, y0) + loc[1] + size // 2 + 0.5
    bg = float(np.median(res))
    if mx < 2.5 * max(bg, 1e-3):
        return None
    # the halfway line: across the pitch (perpendicular to the long axis), through the centre
    best = None
    for yaw in np.arange(yaw_deg - yaw_tol, yaw_deg + yaw_tol + 1e-6, 0.25):
        a = math.radians(yaw)
        # the plan's z axis in image coordinates: (sin a, cos a)
        ux, uy = math.sin(a), math.cos(a)
        ts = np.linspace(-width_m / 2 + 4, width_m / 2 - 4, 120) * ppm
        xs = cx + ts * ux
        ys = cy + ts * uy
        m = cv2.remap(tophat, xs.astype(np.float32).reshape(1, -1), ys.astype(np.float32).reshape(1, -1), cv2.INTER_LINEAR)
        # keep out of the circle itself, where the ring would dominate
        keep = np.abs(ts) > (r + 3)
        sc = float(m[0][keep].mean())
        if best is None or sc > best[0]:
            best = (sc, yaw)
    e2 = box[0] + cx / ppm
    n2 = box[3] - cy / ppm
    return dict(e=e2, n=n2, yaw_deg=best[1], ring_score=float(mx / max(bg, 1e-3)), line_score=float(best[0]))


def outline_edge_offsets(img, box, ppm, ring_lv95, search_m=5.0, step_m=1.0):
    """How far a placed outline is from the nearest strong edge of the aerial, along its normals (metres).

    For points every step_m along the ring, the grey gradient is sampled across the outline within +-search_m; the
    offset of its largest value is that point's distance to the visible edge. Returns (median, p90, n, rms).
    """
    gray = cv2.GaussianBlur(cv2.cvtColor(img, cv2.COLOR_RGB2GRAY).astype(np.float32), (0, 0), 1.5)
    ring = Polygon(ring_lv95).exterior
    offs = []
    ts = np.arange(0, ring.length, step_m)
    d = np.arange(-search_m, search_m + 1e-6, 0.25)
    for t in ts:
        a = ring.interpolate(t)
        b = ring.interpolate(min(ring.length, t + 0.5))
        c0 = ring.interpolate(max(0.0, t - 0.5))
        tx, ty = b.x - c0.x, b.y - c0.y
        nrm = math.hypot(tx, ty) or 1.0
        nx, ny = -ty / nrm, tx / nrm
        xs = np.array([aerial.lv95_to_px(a.x + nx * k, a.y + ny * k, box, ppm)[0] for k in d], np.float32)
        ys = np.array([aerial.lv95_to_px(a.x + nx * k, a.y + ny * k, box, ppm)[1] for k in d], np.float32)
        if xs.min() < 1 or ys.min() < 1 or xs.max() >= gray.shape[1] - 1 or ys.max() >= gray.shape[0] - 1:
            continue
        prof = cv2.remap(gray, xs.reshape(1, -1), ys.reshape(1, -1), cv2.INTER_LINEAR)[0]
        g = np.abs(np.gradient(prof))
        offs.append(abs(float(d[int(np.argmax(g))])))
    if not offs:
        return None
    return float(np.median(offs)), float(np.percentile(offs, 90)), len(offs), float(np.sqrt(np.mean(np.square(offs))))

"""Is a scan's placement the right building? Two checks that do not use the footprint fit's score.

Identity: the solid the outline was fitted to must be the venue's building: it contains, or lies within IDENT_M of, the GWR entrance of the venue's
street address (hint name "..., Street 12") or the GWR building point of a building whose name (GBEZ) matches the venue; failing both, the hint's own
geocoded point must lie in or at it (weaker; a hint marked `unsure` never confirms).
Aerial: the placed outline against roof edges of the swissimage aerial. The share of its length within EDGE_TOL_M of a strong edge is compared with the
same share for copies shifted by 4-12 m in eight directions and turned by 15, 30 and 90 degrees; it must beat their 90th percentile.
The edges are long chains (5 m or more) of a strong Canny edge whose gradient runs across the outline, so texture (trees, tiles) and lines along it do not count. The aerial is not a
true orthophoto, so a tall solid (over TALL_M) leans away from its footprint: it is flagged and decided on identity alone, and only a strong identity
(address or name) counts then.
"""
import math
import re
import sqlite3
import unicodedata

import cv2
import numpy as np
from shapely import affinity
from shapely.geometry import Point, Polygon

import aerial
import place
import tiles

IDENT_M = 5.0
EDGE_TOL_M = 1.0
MIN_SCORE = 0.40
MIN_LIFT = 0.10  # over the 90th percentile of the displaced copies
TALL_M = 30.0
SHIFTS_M = (4.0, 7.0, 12.0)
TURNS_DEG = (-30.0, -15.0, 15.0, 30.0, 90.0)
MARGIN_M = 25.0


def _norm(s):
    s = unicodedata.normalize("NFKD", s or "").encode("ascii", "ignore").decode().lower()
    return re.sub(r"[^a-z0-9]+", " ", s).strip()


def address_of(name):
    """(street, number) from a hint name such as 'UniS, Schanzeneckstrasse 1' or 'Messe Basel, Messeplatz 10 (the new hall)'; None when there is none."""
    for seg in reversed([s.strip() for s in re.sub(r"\(.*?\)", "", name or "").split(",")]):
        m = re.match(r"^(.+?)\s+(\d+[a-zA-Z]?)$", seg)
        if m and not re.search(r"\d", m.group(1)):
            return m.group(1).strip(), m.group(2)
    return None


def gwr_address_points(gwr, name, town=None):
    """[(E, N, EGID)] of the GWR entrances at the hint's street address; [] when there is no address or no hit."""
    ad = address_of(name)
    if not ad or not gwr:
        return []
    street, num = ad
    con = sqlite3.connect("file:%s?mode=ro" % gwr.replace("\\", "/"), uri=True)
    try:
        rows = con.execute("select DKODE, DKODN, EGID, STRNAME, DPLZNAME from entrance where DEINR=? and DKODE is not null and "
                           "(lower(STRNAME)=lower(?) or lower(STRNAME) like lower(?))", (num, street, street + "%")).fetchall()
    finally:
        con.close()
    if town:
        t = _norm(town)
        near = [r for r in rows if _norm(r[4]).startswith(t) or t.startswith(_norm(r[4]))]
        rows = near or []
    return [(r[0], r[1], r[2]) for r in rows]


def gwr_named_points(gwr, name, at, radius=250.0):
    """[(E, N, EGID, GBEZ)] of GWR buildings near the hint whose name (GBEZ) shares a word of 5+ letters with the venue name."""
    if not gwr or not at:
        return []
    words = {w for w in _norm(re.sub(r"\(.*?\)", "", name or "").split(",")[0]).split() if len(w) >= 5}
    if not words:
        return []
    con = sqlite3.connect("file:%s?mode=ro" % gwr.replace("\\", "/"), uri=True)
    try:
        rows = con.execute("select GKODE, GKODN, EGID, GBEZ from building where GBEZ is not null and GKODE between ? and ? and GKODN between ? and ?",
                           (at[0] - radius, at[0] + radius, at[1] - radius, at[1] + radius)).fetchall()
    finally:
        con.close()
    return [r for r in rows if words & set(_norm(r[3]).split())]


def identity(fp, egid, info, gwr=None):
    """How well the solid `fp` (EGID `egid`) is the venue's building, from the GWR and the hint. Returns dict(verdict, how, dist_m, detail).

    verdict: "match" (address or name point at the solid), "point" (only the geocoded hint point is in it), "mismatch" (an address or name was found
    and every point lies farther than IDENT_M from the solid), "unconfirmed" (nothing independent to compare with)."""
    name = info.get("name") or ""
    pts = gwr_address_points(gwr, name, info.get("town"))
    how = "address"
    if not pts:
        pts = [(e, n, g) for e, n, g, _ in gwr_named_points(gwr, name, info.get("at"))]
        how = "name"
    if pts:
        ds = [(fp.distance(Point(e, n)), g) for e, n, g in pts]
        d, g = min(ds)
        if d <= IDENT_M:
            return dict(verdict="match", how=how, dist_m=round(d, 1), detail="GWR %s point of EGID %s is %.1f m from the solid (EGID %s)" % (how, g, d, egid))
        return dict(verdict="mismatch", how=how, dist_m=round(d, 1), detail="the nearest GWR %s point (EGID %s) is %.1f m from the solid (EGID %s)" % (how, g, d, egid))
    at = info.get("at")
    if at and not info.get("unsure"):
        d = fp.distance(Point(at[0], at[1]))
        if d <= IDENT_M:
            return dict(verdict="point", how="geocoded hint", dist_m=round(d, 1), detail="no GWR address or name hit; the geocoded hint point is %.1f m from the solid" % d)
        return dict(verdict="mismatch", how="geocoded hint", dist_m=round(d, 1), detail="the geocoded hint point is %.1f m from the solid (EGID %s)" % (d, egid))
    return dict(verdict="unconfirmed", how=None, dist_m=None, detail="no street address or GWR name to compare, and the hint position is %s" % ("marked unsure" if at else "absent"))


# ----------------------------------------------------------------------------------------------------- aerial
BINS = 12  # orientations of an edge (gradient direction mod 180 degrees), 15 degrees each
EDGE_PCT = 98.0  # Canny high threshold = this percentile of the picture's gradient magnitude
MIN_CHAIN_M = 5.0  # edge chains shorter than this are texture (trees, tiles, cars), not a roof edge


def edge_maps(rgb, ppm):
    """Roof-edge candidates of an aerial as BINS masks, one per gradient orientation: Canny edges (high threshold from the picture's own gradient
    percentile), chains of at least MIN_CHAIN_M only, each pixel in its own orientation bin and the two beside it."""
    g = cv2.GaussianBlur(cv2.cvtColor(np.ascontiguousarray(rgb), cv2.COLOR_RGB2GRAY), (0, 0), 1.0)
    gx, gy = cv2.Sobel(g, cv2.CV_32F, 1, 0), cv2.Sobel(g, cv2.CV_32F, 0, 1)
    mag = np.hypot(gx, gy)
    hi = float(np.percentile(mag, EDGE_PCT))
    e = cv2.Canny(g, max(hi * 0.4, 8.0) / 4.0, max(hi, 30.0) / 4.0, L2gradient=True)
    n, lab, st, _ = cv2.connectedComponentsWithStats(e, connectivity=8)
    keep = np.zeros(n, bool)
    keep[1:] = np.maximum(st[1:, cv2.CC_STAT_WIDTH], st[1:, cv2.CC_STAT_HEIGHT]) >= MIN_CHAIN_M * ppm
    e = np.where(keep[lab], 255, 0).astype(np.uint8)
    ang = np.mod(np.arctan2(gy, gx), np.pi)
    b = np.minimum((ang / np.pi * BINS).astype(int), BINS - 1)
    out = []
    for k in range(BINS):
        near = (b == k) | (b == (k + 1) % BINS) | (b == (k - 1) % BINS)
        out.append(cv2.distanceTransform(255 - np.where(near, e, 0).astype(np.uint8), cv2.DIST_L2, 3))
    return out, e


def outline_score(poly, dists, box, ppm, tol_m=None, step_m=0.5):
    """Share of the polygon's outline (sampled every `step_m`) that has, within `tol_m`, an edge chain whose gradient runs across the outline (the
    outline is the roof edge: the picture changes across it)."""
    tol_m = EDGE_TOL_M if tol_m is None else tol_m
    ring = poly.exterior
    n = max(8, int(ring.length / step_m))
    ts = np.linspace(0, ring.length, n, endpoint=False)
    pts = [ring.interpolate(t) for t in ts]
    ahead = [ring.interpolate((t + 0.5) % ring.length) for t in ts]
    behind = [ring.interpolate((t - 0.5) % ring.length) for t in ts]
    tx = np.array([a.x - b.x for a, b in zip(ahead, behind)])
    ty = np.array([a.y - b.y for a, b in zip(ahead, behind)])
    xs, ys = aerial.lv95_to_px(np.array([p.x for p in pts]), np.array([p.y for p in pts]), box, ppm)
    h, w = dists[0].shape
    ok = (xs >= 0) & (xs < w) & (ys >= 0) & (ys < h)
    if ok.sum() < 0.8 * n:
        return None  # the outline leaves the aerial
    # the normal of the outline in image terms (x east, y south), as a gradient direction mod 180 degrees
    nang = np.mod(np.arctan2(tx, ty), np.pi)  # normal = (ty, -tx) in (E, N); image y runs south, so its angle is atan2(tx, ty)
    bins = np.minimum((nang / np.pi * BINS).astype(int), BINS - 1)
    d = np.array([dists[b][int(y), int(x)] for b, x, y in zip(bins[ok], xs[ok], ys[ok])])
    return float((d <= tol_m * ppm).mean())


def displaced(poly):
    out = []
    for r in SHIFTS_M:
        for k in range(8):
            a = k * math.pi / 4
            out.append(affinity.translate(poly, r * math.cos(a), r * math.sin(a)))
    for t in TURNS_DEG:
        out.append(affinity.rotate(poly, t, origin="centroid"))
    return out


def aerial_agreement(poly, rgb, box, ppm):
    """dict(score, null_p90, null_median, null_max, lift) of the placed outline `poly` (LV95) in the aerial `rgb` over `box`; None score when it leaves the picture."""
    dists, edges = edge_maps(rgb, ppm)
    s = outline_score(poly, dists, box, ppm)
    nulls = [x for x in (outline_score(q, dists, box, ppm) for q in displaced(poly)) if x is not None]
    if s is None or not nulls:
        return dict(score=None, null_p90=None, null_median=None, null_max=None, lift=None)
    p90 = float(np.percentile(nulls, 90))
    return dict(score=round(s, 3), null_p90=round(p90, 3), null_median=round(float(np.median(nulls)), 3), null_max=round(max(nulls), 3), lift=round(s - p90, 3),
                edge_density=round(float((edges > 0).mean()), 4))

def placed_outline(cue):
    """The cue's level-0 outline in LV95."""
    p = cue["placement"]
    return Polygon([place.to_lv95(x, z, p["e"], p["n"], p["yaw_deg"]) for x, z in cue["levels"][0]["outline"]]).buffer(0)


def aerial_check(poly, cache_dir, height_m=None):
    """Fetch the aerial round the placed outline and score it. `tall` is set above TALL_M (roofs lean in the aerial)."""
    e0, n0, e1, n1 = poly.buffer(MARGIN_M).bounds
    rgb, box, ppm = aerial.fetch(e0, n0, e1, n1, cache_dir)
    r = aerial_agreement(poly, rgb, box, ppm)
    r["tall"] = bool(height_m is not None and height_m > TALL_M)
    r["height_m"] = None if height_m is None else round(float(height_m), 1)
    r["passes"] = bool(r["score"] is not None and r["score"] >= MIN_SCORE and r["lift"] >= MIN_LIFT)
    return r


def verdict(ident, aer):
    """("ok" | "ok (tall, identity only)" | "wrong_building?", reason): the placement counts only when identity and aerial agree.

    A tall solid's aerial is not decisive (it leans): then only a GWR address or name match counts."""
    if ident["verdict"] == "mismatch":
        return "wrong_building?", "identity: " + ident["detail"]
    if ident["verdict"] == "unconfirmed":
        return "wrong_building?", "identity not confirmed: " + ident["detail"]
    if aer.get("score") is None:
        return "wrong_building?", "aerial: the placed outline leaves the aerial picture"
    if aer["passes"]:
        return "ok", "identity %s (%s); aerial edge score %.2f against %.2f for displaced copies" % (ident["verdict"], ident["how"], aer["score"], aer["null_p90"])
    if aer["tall"]:
        if ident["verdict"] == "match":
            return "ok (tall, identity only)", "tall solid (%.0f m: roofs lean in the aerial), decided on identity (%s: %s)" % (aer["height_m"], ident["how"], ident["detail"])
        return "wrong_building?", "tall solid (%.0f m): the aerial is not decisive and identity is only the geocoded point" % aer["height_m"]
    return "wrong_building?", "aerial: the outline follows roof edges no better than displaced copies (score %.2f, copies' 90th percentile %.2f, needs %.2f and a lift of %.2f)" % (
        aer["score"], aer["null_p90"], MIN_SCORE, MIN_LIFT)


def solid_of(chunks, placement):
    """The solid a placement was fitted to. An EGID can name two solids (the Kunsthaus has a second wing under the same number), so the one whose
    footprint area is the placement's `footprint_area_m2` is taken."""
    cands = [b for b in tiles.buildings_near(chunks, placement["e"], placement["n"], 80) if b["egid"] == placement["egid"]]
    want = placement.get("footprint_area_m2")
    if not cands:
        return None
    return min(cands, key=lambda b: abs(b["fp"].area - want)) if want else cands[0]
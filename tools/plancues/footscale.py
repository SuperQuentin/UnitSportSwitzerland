"""Scale from the footprint: a plan with no bar and no known object is scaled to the real building when the two clearly share a shape.

The plan's outline (in page points) is searched against the footprints of the buildings near the venue (`place.scale_search`:
scale and turn that maximise the overlap). The scale is accepted only through a gate, because a footprint fit is circular:

1. the overlap is good: IoU and the boundary rms, relative to the building's size;
2. it is the right building: the best candidate beats the next one by a margin (a twin with the same scale does not count against it);
3. the shape is distinctive: the plan beats its own minimum rotated rectangle and convex hull on the same footprint, so a plain box
   or blob is not a match (a near-rectangle is accepted only on its aspect ratio, and only as low_confidence);
4. neither is a part of the other: the plan covers about as much of the footprint as the footprint covers of the plan.

A written ratio ("1:200") that agrees with the scale within a few percent (the PDF's page size is the paper size) confirms it: ok.
Otherwise low_confidence. `BIAS` is the measured ratio of the bar's scale to the footprint's (see docs/notes/tools/plancues.md).
"""
import math
import re

import place

PT_M = 0.0254 / 72.0  # metres of paper per page point
STANDARD_RATIOS = (20, 25, 50, 75, 100, 125, 150, 200, 250, 300, 400, 500, 750, 1000, 1250, 1500, 2000, 2500, 5000)

# Thresholds, set from the validation pages (see the note).
THR = dict(
    iou=0.85,           # overlap of the plan and the footprint
    rms_frac=0.02,      # boundary rms, as a share of sqrt(footprint area)
    margin=0.05,        # best candidate's IoU over the next one's
    twin_scale=0.03,    # a runner-up whose scale agrees this closely does not threaten the scale
    distinct=0.03,      # the plan's IoU over the best of its rectangle and hull
    balance=0.12,       # plan_cover - footprint_cover, absolute: one is a part of the other beyond this
    aspect_tol=0.04,    # a near-rectangle: the two aspect ratios agree within this
    min_aspect=1.3,     # and the rectangle is at least this elongated
    ratio_tol=0.05,     # written ratio vs the footprint's scale
    m_min=0.015, m_max=1.5,  # metres per point a drawing can plausibly have (1:50 on A4 ... 1:4000 on A3)
)
RELAXED = dict(iou=0.7, rms_frac=1.0, margin=-1.0, distinct=-1.0, balance=1.0, m_min=0.0, m_max=1e9)  # review drawings of near misses only
BIAS = 1.0  # footprint scale x BIAS = best estimate of the scale


def written_ratios(words):
    """Ratios written on a page ('1:200', '1 : 500'): the plausible architectural ones only."""
    text = " ".join(w[4] for w in words)
    out = []
    for m in re.finditer(r"(?<![\d.,:])1\s*:\s*(\d{2,4})(?![\d:])", text):
        n = int(m.group(1))
        if n in STANDARD_RATIOS and n not in out:
            out.append(n)
    return out


def ratio_check(m_per_pt, ratios, tol=THR["ratio_tol"]):
    """The written ratio nearest the scale and whether it agrees within tol: dict(ratio, implied_m_per_pt, error) or None."""
    if not ratios:
        return None
    best = min(ratios, key=lambda r: abs(math.log(r * PT_M / m_per_pt)))
    implied = best * PT_M
    return dict(method="written_ratio", ratio="1:%d" % best, implied_m_per_pt=implied, error=implied / m_per_pt - 1,
                agrees=abs(implied / m_per_pt - 1) <= tol)


def _aspect(poly):
    """Length over width of the minimum rotated rectangle (>= 1)."""
    c = list(poly.minimum_rotated_rectangle.exterior.coords)
    a, b = math.dist(c[0], c[1]), math.dist(c[1], c[2])
    return max(a, b) / max(min(a, b), 1e-9)


def fit_candidate(poly, b, refine=True):
    """Scale search of the plan against one building: dict(egid, fp, fit, s, iou, rms_m)."""
    r, s = place.scale_search(poly, b["fp"], refine=refine)
    return dict(egid=int(b["egid"]), fp=b["fp"], fit=r, s=s, iou=r["iou"], rms_m=r["rms_m"])


def judge(poly, cands, thr=None, bias=None, words=None, refs=True):
    """Scale of a plan outline (page points, any origin) from candidate buildings (dicts with 'egid' and 'fp').

    Returns dict(status, reasons, m_per_pt (None when rejected), egid, iou, rms_m, margin, distinct, cross_check, ranked, fit)
    where status is ok / low_confidence / rejected. The gate is described in the module docstring.
    """
    thr = dict(THR, **(thr or {}))
    bias = BIAS if bias is None else bias
    res = dict(status="rejected", reasons=[], m_per_pt=None, egid=None, iou=None, rms_m=None, margin=None, distinct=None,
               cross_check=None, ranked=[], fit=None)
    if poly is None or poly.is_empty or not cands:
        res["reasons"].append("no outline" if poly is None or poly.is_empty else "no building near the position hint")
        return res
    ranked = sorted((fit_candidate(poly, b) for b in cands), key=lambda c: -c["iou"])
    res["ranked"] = [dict(egid=c["egid"], iou=round(c["iou"], 4), m_per_pt=c["s"], rms_m=round(c["rms_m"], 3),
                          area_m2=round(c["fp"].area, 1)) for c in ranked[:5]]
    top = ranked[0]
    r, s, fp = top["fit"], top["s"], top["fp"]
    res.update(egid=top["egid"], iou=top["iou"], rms_m=top["rms_m"], fit=r, m_per_pt=s * bias, raw_m_per_pt=s, footprint_area_m2=fp.area)
    why = []
    rms_lim = thr["rms_frac"] * math.sqrt(fp.area)
    if top["iou"] < thr["iou"]:
        why.append("overlap with the best building is IoU %.2f (needs %.2f)" % (top["iou"], thr["iou"]))
    if top["rms_m"] > rms_lim:
        why.append("boundary rms %.2f m is over %.2f m (%.0f %% of the footprint's size)" % (top["rms_m"], rms_lim, 100 * thr["rms_frac"]))
    bal = r["coverage"] - r["footprint_cover"]
    if abs(bal) > thr["balance"]:
        why.append("one is a part of the other: the footprint covers %.0f %% of the plan, the plan %.0f %% of the footprint"
                   % (100 * r["coverage"], 100 * r["footprint_cover"]))
    if not thr["m_min"] <= s * bias <= thr["m_max"]:
        why.append("scale %.3f m per point is not a plausible drawing scale" % (s * bias))
    if len(ranked) > 1:
        nxt = ranked[1]
        res["margin"] = top["iou"] - nxt["iou"]
        twin = abs(nxt["s"] / s - 1) <= thr["twin_scale"]
        res["runner_up_same_scale"] = twin
        if res["margin"] < thr["margin"] and not twin:
            why.append("building %d fits nearly as well (IoU %.2f against %.2f): the plan may be another building"
                       % (nxt["egid"], nxt["iou"], top["iou"]))
    low = []
    if refs == "always" or (refs and not why):
        ref_iou = 0.0
        for name, shape in (("rectangle", poly.minimum_rotated_rectangle), ("hull", poly.convex_hull)):
            ref_iou = max(ref_iou, place.scale_search(shape, fp)[0]["iou"])
        res["ref_iou"] = ref_iou
        res["distinct"] = top["iou"] - ref_iou
        if res["distinct"] < thr["distinct"]:
            ap, af = _aspect(poly), _aspect(fp)
            res["aspect"] = dict(plan=ap, footprint=af)
            if ap < thr["min_aspect"] or abs(ap / af - 1) > thr["aspect_tol"]:
                why.append("a plain shape (a box or blob fits as well: +%.3f IoU only) and the aspect ratios %.2f / %.2f do not settle it"
                           % (res["distinct"], ap, af))
            else:
                low.append("a near-rectangle: only the aspect ratio %.2f / %.2f supports the scale" % (ap, af))
    if why:
        res["reasons"] = why
        res["m_per_pt"] = None
        return res
    cc = ratio_check(s * bias, written_ratios(words or []), thr["ratio_tol"])
    res["cross_check"] = cc
    if cc and cc["agrees"] and not low:
        res["status"] = "ok"
        res["reasons"] = ["footprint scale confirmed by the written %s (%.1f %% apart)" % (cc["ratio"], 100 * cc["error"])]
    else:
        res["status"] = "low_confidence"
        res["reasons"] = low + (["no written ratio agrees with it (nearest %s, %.1f %% apart)" % (cc["ratio"], 100 * cc["error"])] if cc and not cc["agrees"]
                                else ["no written ratio on the page to confirm it"] if not cc else [])
    return res

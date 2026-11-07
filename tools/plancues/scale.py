"""Metres per PDF point.

Order of trust: a graphic scale bar (vector ticks and the numbers beside them), then a known object (a football
pitch is 105 x 68 m, an ice rink 60 x 30 m), then, in place.py, a fit of the outline to the building's footprint.
A written "1:100" alone is never used: sheets are shrunk when they are printed to fit another paper size.
"""
import re

import numpy as np

NUM = re.compile(r"^(\d{1,4}(?:[.,]\d+)?)\s*(km|m)?$", re.I)


def _numeric_words(words, clip=None):
    out = []
    for x0, y0, x1, y1, t in words:
        m = NUM.match(t.strip())
        if not m:
            continue
        if clip and not (clip[0] <= (x0 + x1) / 2 <= clip[2] and clip[1] <= (y0 + y1) / 2 <= clip[3]):
            continue
        v = float(m.group(1).replace(",", "."))
        if m.group(2) and m.group(2).lower() == "km":
            v *= 1000
        out.append(dict(v=v, x0=x0, y0=y0, x1=x1, y1=y1, unit=(m.group(2) or "").lower()))
    return out


def vertical_edges(drawings):
    """x positions of short vertical strokes and rectangle sides that could be bar ticks: (x, y0, y1)."""
    ticks = []
    for d in drawings:
        r = d["rect"]
        for it in d["items"]:
            if it[0] == "l":
                a, b = it[1], it[2]
                if abs(a[0] - b[0]) < 0.05 and 1.2 < abs(a[1] - b[1]) < 30:
                    ticks.append((float(a[0]), float(min(a[1], b[1])), float(max(a[1], b[1]))))
            elif it[0] == "re":
                rc = it[1]
                if 1.0 < rc[3] - rc[1] < 30 and rc[2] - rc[0] < 400:
                    ticks.append((float(rc[0]), float(rc[1]), float(rc[3])))
                    ticks.append((float(rc[2]), float(rc[1]), float(rc[3])))
            elif it[0] == "qu":
                q = it[1]
                xs_, ys_ = [p[0] for p in q], [p[1] for p in q]
                rc = (min(xs_), min(ys_), max(xs_), max(ys_))
                if 1.0 < rc[3] - rc[1] < 30:
                    ticks.append((float(rc[0]), float(rc[1]), float(rc[3])))
                    ticks.append((float(rc[2]), float(rc[1]), float(rc[3])))
    return ticks


def _fit(pairs):
    """Least squares value = a * x + b through (x, value); returns a, b, worst residual in value units."""
    x = np.array([p[0] for p in pairs])
    v = np.array([p[1] for p in pairs])
    a, b = np.polyfit(x, v, 1)
    return float(a), float(b), float(np.abs(a * x + b - v).max())


def find_scale_bar(words, drawings, clip=None):
    """The best graphic scale bar of a page (or of a clip rectangle): dict or None.

    Numbers in a row (at least two, increasing) are matched to the vertical tick positions just above or below
    them; the anchor (left edge, centre or right edge of the number) with the smallest residual wins.
    """
    nums = _numeric_words(words, clip)
    if len(nums) < 2:
        return None
    ticks = vertical_edges(drawings)
    if not ticks:
        return None
    rows = {}
    for n in nums:
        rows.setdefault(round(((n["y0"] + n["y1"]) / 2) / 3), []).append(n)
    best = None
    for key, row in rows.items():
        for k2 in (key - 1, key + 1):  # a row straddling two buckets
            row = row + [n for n in rows.get(k2, []) if abs((n["y0"] + n["y1"]) / 2 - (row[0]["y0"] + row[0]["y1"]) / 2) < 2.5]
        row = sorted({id(n): n for n in row}.values(), key=lambda n: n["x0"])
        if len(row) < 2 or len({n["v"] for n in row}) < 2:
            continue
        ymid = (row[0]["y0"] + row[0]["y1"]) / 2
        y0r, y1r = min(n["y0"] for n in row), max(n["y1"] for n in row)
        near = [t for t in ticks if max(t[1] - y1r, y0r - t[2]) < 5 and t[2] - t[1] < 30]
        if len(near) < 2:
            continue
        xs = sorted({round(t[0], 2) for t in near})
        for anchor in ("x0", "c", "x1"):
            pairs = []
            for n in row:
                ax = n["x0"] if anchor == "x0" else n["x1"] if anchor == "x1" else (n["x0"] + n["x1"]) / 2
                j = int(np.argmin([abs(x - ax) for x in xs]))
                tol = max(2.5, 0.8 * (n["x1"] - n["x0"]))
                if abs(xs[j] - ax) <= tol:
                    pairs.append((xs[j], n["v"]))
            pairs = sorted(set(pairs))
            if len(pairs) < 2 or len({p[1] for p in pairs}) < 2 or len({p[0] for p in pairs}) < 2:
                continue
            vals = [p[1] for p in pairs]
            if vals != sorted(vals):
                continue
            a, b, res = _fit(pairs)
            span = max(vals) - min(vals)
            if a <= 0 or res > 0.01 * span + 0.05:
                continue
            score = (len(pairs), -res / span)
            if best is None or score > best[0]:
                best = (score, dict(m_per_pt=a, labels=[dict(v=p[1], x=p[0]) for p in pairs], resid_m=res,
                                    n_labels=len(pairs), anchor=anchor, y=ymid, span_m=span,
                                    x_range=(min(p[0] for p in pairs), max(p[0] for p in pairs))))
    return best[1] if best else None


def find_known_objects(drawings, page_rect, kinds=("pitch",), clip=None):
    """Drawings whose box has the proportions of a football pitch (105 x 68 m) or an ice rink (60 x 30 m).

    A pitch is confirmed when a halfway line crosses its box at the middle; a rink is much weaker evidence.
    Returns candidates best first: dict(kind, rect, m_per_pt_long, m_per_pt_short, evidence).
    """
    specs = {"pitch": (105.0, 68.0, 0.07, 0.06), "rink": (60.0, 30.0, 0.05, 0.04)}
    minside = 0.06 * max(page_rect.width, page_rect.height)
    out = []
    for kind in kinds:
        L, W, tol, wtol = specs[kind]
        for d in drawings:
            r = d["rect"]
            if clip and not (clip[0] <= r.x0 and r.x1 <= clip[2] and clip[1] <= r.y0 and r.y1 <= clip[3]):
                continue
            w, h = max(r.width, r.height), min(r.width, r.height)
            if h < 1 or w < minside or w > 0.98 * max(page_rect.width, page_rect.height):
                continue
            if abs(w / h - L / W) > tol * L / W:
                continue
            horizontal = r.width >= r.height
            mid = (r.x0 + r.x1) / 2 if horizontal else (r.y0 + r.y1) / 2
            half = False
            for e in drawings:
                q = e["rect"]
                if horizontal and q.width < 2.5 and abs((q.x0 + q.x1) / 2 - mid) < 0.02 * w and q.height > 0.85 * h \
                        and q.y0 >= r.y0 - 2 and q.y1 <= r.y1 + 2:
                    half = True
                    break
                if not horizontal and q.height < 2.5 and abs((q.y0 + q.y1) / 2 - mid) < 0.02 * w and q.width > 0.85 * h \
                        and q.x0 >= r.x0 - 2 and q.x1 <= r.x1 + 2:
                    half = True
                    break
            circle = False
            cx0, cy0 = (r.x0 + r.x1) / 2, (r.y0 + r.y1) / 2
            for e in drawings:  # the centre circle: a round path 17-19.5 % of the pitch's length across, at its middle
                q = e["rect"]
                if abs(q.width - q.height) < 0.06 * q.width and 0.15 * w < q.width < 0.2 * w                         and abs((q.x0 + q.x1) / 2 - cx0) < 0.03 * w and abs((q.y0 + q.y1) / 2 - cy0) < 0.03 * w                         and any(i[0] == "c" for i in e["items"]):
                    circle = True
                    break
            if kind == "pitch" and not (half and circle):
                continue
            out.append(dict(kind=kind, rect=tuple(float(v) for v in r), horizontal=horizontal,
                            m_per_pt_long=L / w, m_per_pt_short=W / h, halfway_line=half, centre_circle=circle,
                            evidence="aspect %.3f vs %.3f" % (w / h, L / W)))
    out.sort(key=lambda c: (-int(c["halfway_line"] and c["centre_circle"]), abs(c["m_per_pt_long"] / c["m_per_pt_short"] - 1)))
    return out


def decide(words, drawings, page_rect, clip=None, kinds=("pitch",)):
    """The scale of a page: the graphic bar if there is one, else a known object; the other, when present, is the cross-check."""
    bar = find_scale_bar(words, drawings, clip)
    objs = find_known_objects(drawings, page_rect, kinds, clip)
    res = dict(m_per_pt=None, method=None, cross_check=None, bar=bar, objects=objs[:3])
    if bar:
        res["m_per_pt"], res["method"] = bar["m_per_pt"], "scale_bar"
        if objs:
            o = objs[0]
            lng = o["m_per_pt_long"]
            res["cross_check"] = dict(method="known_object", kind=o["kind"], m_per_pt=lng,
                                      ratio=bar["m_per_pt"] / lng, rect=o["rect"])
    elif objs:
        o = objs[0]
        res["m_per_pt"], res["method"] = (o["m_per_pt_long"] + o["m_per_pt_short"]) / 2, "known_object"
    return res

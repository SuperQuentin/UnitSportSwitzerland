"""Measurements from a long section drawn without a scale: scaled by the building's length, known from the plan.

The section's building is as long as the plan's roof (outer edge to outer edge), so its length in points gives the
sheet's metres per point (method `cross_sheet`; there is no bar and no readable scale on the sheet). Then: the pitch
level (the long heavy horizontal line), the roof's height above it, and the steps of the heavy stepped profile.
"""
import statistics

import numpy as np


def _dark(x):
    return x.get("color") is not None and sum(x["color"]) < 0.3


def measure(drawings, building_length_m, pitch_length_m=105.0):
    cut = [d for d in drawings if d["type"] in ("s", "fs") and (d.get("width") or 0) >= 0.29 and _dark(d)]
    # the pitch level: the longest heavy horizontal line
    hl = [d for d in cut if d["rect"].height < 0.2 and d["rect"].width > 100]
    if not hl:
        return None
    ground = max(hl, key=lambda d: d["rect"].width)
    gy = ground["rect"].y0
    gx0, gx1 = ground["rect"].x0, ground["rect"].x1
    # the end stands lie within a stand's depth (a fifth of the ground line) beyond either end of the ground line
    stands = [d for d in cut if d["rect"].height > 5 and d["rect"].y1 > gy - 5 and gx0 - 0.3 * (gx1 - gx0) < d["rect"].x0 and d["rect"].x1 < gx1 + 0.3 * (gx1 - gx0)]
    if len(stands) < 2:
        return None
    x0 = min(d["rect"].x0 for d in stands)
    # the building spans from the left stand's outer wall to the right one's, about the ground line's middle
    mid = (ground["rect"].x0 + ground["rect"].x1) / 2
    x1 = 2 * mid - x0
    length_pt = x1 - x0
    m = building_length_m / length_pt
    out = dict(m_per_pt=m, method="cross_sheet", length_pt=length_pt, pitch_level_y=gy,
               ground_line_m=ground["rect"].width * m)
    # the roof at the facade and its highest point over the end stand, from every path over the first tenth of the length
    near = [d for d in drawings if d["rect"].x0 >= x0 - 2 and d["rect"].x1 <= x0 + 0.12 * length_pt and d["rect"].y0 < gy - 20]
    if near:
        top = min(d["rect"].y0 for d in near)
        out["roof_top_m"] = (gy - top) * m
        edge = [d for d in near if d["rect"].x0 <= x0 + 4]
        out["roof_edge_m"] = (gy - min(d["rect"].y0 for d in edge)) * m if edge else None
    # the stepped profile: the heavy path with the most items that is mostly horizontal and vertical runs
    best = None
    for d in cut:
        its = [i for i in d["items"] if i[0] == "l"]
        if len(its) < 20:
            continue
        ax = sum(1 for i in its if abs(i[1][0] - i[2][0]) < 0.05 or abs(i[1][1] - i[2][1]) < 0.05)
        if ax / len(its) >= 0.8 and (best is None or len(its) > best[0]):
            best = (len(its), its)
    if best:
        rises = sorted(abs(i[1][1] - i[2][1]) * m for i in best[1] if abs(i[1][0] - i[2][0]) < 0.05 and abs(i[1][1] - i[2][1]) > 0.05)
        runs = sorted(abs(i[1][0] - i[2][0]) * m for i in best[1] if abs(i[1][1] - i[2][1]) < 0.05 and abs(i[1][0] - i[2][0]) > 0.05)
        # steps come in pairs of a riser and a tread: keep the dominant size, not the thin detail lines
        rises = [r for r in rises if r > 0.15]
        runs = [r for r in runs if r > 0.3]
        if rises and runs:
            out["steps"] = dict(count=min(len(rises), len(runs)), riser_m=statistics.median(rises), tread_m=statistics.median(runs))
    return out

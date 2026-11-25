"""Review overlays: the cue drawn over the swissimage aerial (3 px/m) and over its source page."""
import os

import cv2
import numpy as np

import aerial
import pdfio
import place

OUTLINE = (255, 40, 40)
ROOM = (40, 120, 255)
DOOR = (255, 0, 255)
FRONT = (0, 255, 255)
ROOF = (255, 170, 0)
PITCH = (255, 255, 255)


def _placed(c, pts):
    p = c["placement"]
    return [place.to_lv95(x, z, p["e"], p["n"], p["yaw_deg"]) for x, z in pts]


def overlay_aerial(c, cache_dir, out_png, margin=40.0):
    """Cue over the aerial; returns the PNG path or None when the cue has no placement."""
    if not c.get("placement") or not c.get("levels"):
        return None
    lvl = c["levels"][0]
    ring = _placed(c, lvl["outline"])
    es, ns = [p[0] for p in ring], [p[1] for p in ring]
    img, box, ppm = aerial.fetch(min(es) - margin, min(ns) - margin, max(es) + margin, max(ns) + margin, cache_dir)
    img = np.ascontiguousarray(img[:, :, ::-1])  # BGR for cv2 drawing

    def px(points):
        return np.round(np.array([aerial.lv95_to_px(e, n, box, ppm) for e, n in points]) * 16).astype(np.int32)

    def poly(points, col, w=2, closed=True):
        cv2.polylines(img, [px(points)], closed, col, w, cv2.LINE_AA, 4)

    for r in lvl["rooms"]:
        poly(_placed(c, r["poly"]), ROOM, 1)
    for o in lvl["openings"]:
        poly(_placed(c, [o["a"], o["b"]]), DOOR, 3, closed=False)
    ex = lvl.get("extra", {})
    for key, col in (("roof_outer", ROOF), ("seating_front", FRONT), ("pitch_rect", PITCH)):
        if key in ex:
            (x0, z0), (x1, z1) = ex[key]
            poly(_placed(c, [(x0, z0), (x1, z0), (x1, z1), (x0, z1)]), col, 2)
    poly(ring, OUTLINE, 2)
    p = c["placement"]
    txt = "%s %s  rms %.1f m  cover %.2f" % (c["venue"], c["status"], p["fit_rms_m"], p["coverage"])
    cv2.putText(img, txt, (8, 22), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 0, 0), 3, cv2.LINE_AA)
    cv2.putText(img, txt, (8, 22), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (255, 255, 255), 1, cv2.LINE_AA)
    os.makedirs(os.path.dirname(out_png), exist_ok=True)
    cv2.imwrite(out_png, img)
    return out_png


def overlay_page(c, root, out_png, width_px=1600):
    """Cue (outline, rooms, doors) drawn over the source page render."""
    if not c.get("levels"):
        return None
    src = c["sources"][0]
    doc = pdfio.open_doc(os.path.join(root, src["path"]))
    page = doc[src["page"] - 1]
    z = width_px / page.rect.width
    img = pdfio.render(page, scale=z)
    img = np.ascontiguousarray(img[:, :, ::-1])
    lvl = c["levels"][0]
    m = c["scale"]["m_per_pt"]
    ox, oz = lvl["origin_pt"]

    def px(points):
        return np.round(np.array([((ox + x / m) * z, (oz + y / m) * z) for x, y in points]) * 16).astype(np.int32)

    for r in lvl["rooms"]:
        cv2.polylines(img, [px(r["poly"])], True, ROOM, 1, cv2.LINE_AA, 4)
    for o in lvl["openings"]:
        cv2.polylines(img, [px([o["a"], o["b"]])], False, DOOR, 3, cv2.LINE_AA, 4)
    cv2.polylines(img, [px(lvl["outline"])], True, OUTLINE, 2, cv2.LINE_AA, 4)
    ex = lvl.get("extra", {})
    for key, col in (("roof_outer", ROOF), ("seating_front", FRONT), ("pitch_rect", PITCH)):
        if key in ex:
            (x0, z0), (x1, z1) = ex[key]
            cv2.polylines(img, [px([(x0, z0), (x1, z0), (x1, z1), (x0, z1)])], True, col, 2, cv2.LINE_AA, 4)
    os.makedirs(os.path.dirname(out_png), exist_ok=True)
    cv2.imwrite(out_png, img)
    return out_png

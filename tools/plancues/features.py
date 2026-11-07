"""Cheap per-page features for sorting: vector path statistics, text layer, image coverage, raster statistics, drawing regions."""
import collections
import re

import numpy as np

import pymupdf

import pdfio

CELL = 8  # points per cell of the coverage grid used for regions


def _is_page_background(r, page_rect, fill):
    big = r.width * r.height > 0.6 * page_rect.width * page_rect.height
    white = fill is not None and min(fill) > 0.95
    return big and (white or fill is None)


def vector_stats(page):
    dr = page.get_cdrawings()
    for d in dr:
        d["rect"] = pymupdf.Rect(d["rect"])
    widths = collections.Counter()
    n_lines = n_fill = n_dash = n_curve = 0
    for d in dr:
        for it in d["items"]:
            if it[0] == "l":
                n_lines += 1
            elif it[0] == "c":
                n_curve += 1
        if d.get("fill") is not None and d.get("type") in ("f", "fs"):
            n_fill += 1
        if d.get("dashes") and d["dashes"] not in ("[] 0", ""):
            n_dash += 1
        if d.get("type") in ("s", "fs") and d.get("width"):
            widths[round(d["width"], 2)] += 1
    return dict(n_paths=len(dr), n_lines=n_lines, n_curves=n_curve, n_fill=n_fill, n_dashed=n_dash,
                widths=widths.most_common(6)), dr


def image_boxes(page):
    out = []
    try:
        for info in page.get_image_info():
            b = info["bbox"]
            if b[2] - b[0] > 2 and b[3] - b[1] > 2:
                out.append(tuple(b))
    except Exception:
        pass
    return out


def coverage(page_rect, boxes):
    """Fraction of the page covered by a union of boxes (grid estimate)."""
    if not boxes:
        return 0.0
    w, h = int(page_rect.width / CELL) + 1, int(page_rect.height / CELL) + 1
    g = np.zeros((h, w), bool)
    for x0, y0, x1, y1 in boxes:
        g[max(0, int(y0 / CELL)):int(y1 / CELL) + 1, max(0, int(x0 / CELL)):int(x1 / CELL) + 1] = True
    return float(g.mean())


def raster_stats(rgb):
    """Statistics of a small RGB render: how white, how colourful, how rectilinear."""
    import cv2
    h, w = rgb.shape[:2]
    s = 320.0 / max(h, w)
    if s < 1:
        rgb = cv2.resize(rgb, (int(w * s), int(h * s)), interpolation=cv2.INTER_AREA)
    gray = cv2.cvtColor(rgb, cv2.COLOR_RGB2GRAY)
    hsv = cv2.cvtColor(rgb, cv2.COLOR_RGB2HSV)
    white = gray > 225
    nonwhite = ~white
    sat = float(hsv[:, :, 1][nonwhite].mean() / 255) if nonwhite.any() else 0.0
    gx = cv2.Sobel(gray, cv2.CV_32F, 1, 0)
    gy = cv2.Sobel(gray, cv2.CV_32F, 0, 1)
    mag = np.hypot(gx, gy)
    strong = mag > 60
    if strong.sum() > 50:
        ang = np.degrees(np.arctan2(gy[strong], gx[strong])) % 90
        near_axis = float(np.mean((ang < 8) | (ang > 82)))
    else:
        near_axis = 0.0
    hist = np.histogram(gray, bins=32, range=(0, 255))[0].astype(float)
    p = hist / hist.sum()
    ent = float(-(p[p > 0] * np.log2(p[p > 0])).sum())
    return dict(white=round(float(white.mean()), 3), sat=round(sat, 3), edge=round(float(strong.mean()), 4),
                axis=round(near_axis, 3), entropy=round(ent, 2))


def dhash(rgb, n=16):
    import cv2
    g = cv2.cvtColor(rgb, cv2.COLOR_RGB2GRAY) if rgb.ndim == 3 else rgb
    g = cv2.resize(g, (n + 1, n), interpolation=cv2.INTER_AREA)
    bits = (g[:, 1:] > g[:, :-1]).flatten()
    return int("".join("1" if b else "0" for b in bits), 2)


def hamming(a, b):
    return bin(a ^ b).count("1")


def words_of(page):
    return [(w[0], w[1], w[2], w[3], w[4]) for w in page.get_text("words")]


def drawing_regions(page, drawings, boxes):
    """Bounding boxes (pt) of separate drawings/photos on a sheet: connected blobs of paths and images."""
    import cv2
    r = page.rect
    w, h = int(r.width / CELL) + 1, int(r.height / CELL) + 1
    g = np.zeros((h, w), np.uint8)
    for d in drawings:
        rc = d["rect"]
        if _is_page_background(rc, r, d.get("fill")):
            continue
        if rc.width > 0.5 * r.width and rc.height < 1.5:  # a rule line, not a drawing
            continue
        g[max(0, int(rc.y0 / CELL)):int(rc.y1 / CELL) + 1, max(0, int(rc.x0 / CELL)):int(rc.x1 / CELL) + 1] = 1
    for x0, y0, x1, y1 in boxes:
        g[max(0, int(y0 / CELL)):int(y1 / CELL) + 1, max(0, int(x0 / CELL)):int(x1 / CELL) + 1] = 1
    k = max(3, int(0.025 * max(r.width, r.height) / CELL))
    g = cv2.dilate(g, np.ones((k, k), np.uint8))
    n, lab, stats, _ = cv2.connectedComponentsWithStats(g, connectivity=8)
    out = []
    for i in range(1, n):
        x, y, ww, hh, area = stats[i]
        if ww * hh * CELL * CELL < 0.012 * r.width * r.height:
            continue
        out.append((x * CELL + k * CELL / 2, y * CELL + k * CELL / 2, (x + ww) * CELL - k * CELL / 2, (y + hh) * CELL - k * CELL / 2))
    return [tuple(round(float(v), 1) for v in b) for b in out]


def page_features(doc, pno):
    """All sort features of one page (pno 0-based)."""
    page = doc[pno]
    r = page.rect
    vs, dr = vector_stats(page)
    boxes = image_boxes(page)
    words = words_of(page)
    text = " ".join(w[4] for w in words)
    f = dict(w_pt=round(r.width, 1), h_pt=round(r.height, 1), **vs)
    f["n_words"] = len(words)
    f["n_chars"] = len(text)
    f["text"] = re.sub(r"\s+", " ", text)[:1500]
    f["n_images"] = len(boxes)
    f["img_cover"] = round(coverage(r, boxes), 3)
    f["regions"] = drawing_regions(page, dr, boxes)
    f["image_boxes"] = [tuple(round(v, 1) for v in b) for b in boxes[:40]]
    if dr:
        ext = [d["rect"] for d in dr if not _is_page_background(d["rect"], r, d.get("fill"))]
        if ext:
            x0, y0 = min(e.x0 for e in ext), min(e.y0 for e in ext)
            x1, y1 = max(e.x1 for e in ext), max(e.y1 for e in ext)
            f["draw_cover"] = round(max(0, (x1 - x0) * (y1 - y0)) / (r.width * r.height), 3)
        else:
            f["draw_cover"] = 0.0
    else:
        f["draw_cover"] = 0.0
    rgb = pdfio.render(page, width_px=320)
    f.update(raster_stats(rgb))
    f["dhash"] = "%064x" % dhash(rgb)
    return f

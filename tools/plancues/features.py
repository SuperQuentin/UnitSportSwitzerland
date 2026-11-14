"""Cheap per-page features for sorting: vector path statistics, text layer, image coverage, raster statistics, drawing regions."""
import collections
import re

import numpy as np

import pymupdf

import pdfio

CELL = 8  # points per cell of the coverage grid used for regions
VERSION = 3  # bump when page_features gains keys: the feature cache is keyed on it


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


def raster_shape(rgb, long_side=640):
    """What kind of image is it: a drawing (few flat colours, hard edges, rectilinear straight lines, closed rooms) or a photo?"""
    import cv2
    h, w = rgb.shape[:2]
    s = long_side / float(max(h, w))
    if s < 1:
        rgb = cv2.resize(rgb, (int(w * s), int(h * s)), interpolation=cv2.INTER_AREA)
    gray = cv2.cvtColor(rgb, cv2.COLOR_RGB2GRAY)
    hsv = cv2.cvtColor(rgb, cv2.COLOR_RGB2HSV)
    n = gray.size
    ink = gray < 225
    ink_share = float(ink.mean())
    out = dict(ink=round(ink_share, 4), dark=round(float((gray < 100).mean()), 4), mid=round(float(((gray >= 100) & (gray < 200)).mean()), 4))
    # flat colours: the share of non-white pixels that sit in the 6 most common 3-bit colours
    if ink.any():
        q = ((rgb[:, :, 0] >> 5).astype(np.int32) << 6) | ((rgb[:, :, 1] >> 5).astype(np.int32) << 3) | (rgb[:, :, 2] >> 5).astype(np.int32)
        cnt = np.bincount(q[ink], minlength=512)
        out["flat6"] = round(float(np.sort(cnt)[-6:].sum() / cnt.sum()), 3)
        out["ncol"] = int((cnt > 0.005 * cnt.sum()).sum())
        sat = hsv[:, :, 1][ink] / 255.0
        val = hsv[:, :, 2][ink] / 255.0
        out["vivid"] = round(float(((sat > 0.45) & (val > 0.3)).mean()), 3)
    else:
        out.update(flat6=1.0, ncol=0, vivid=0.0)
    gx = cv2.Sobel(gray, cv2.CV_32F, 1, 0)
    gy = cv2.Sobel(gray, cv2.CV_32F, 0, 1)
    mag = np.hypot(gx, gy)
    any_edge = mag > 40
    out["soft"] = round(float(((mag > 40) & (mag < 150)).sum() / max(1, any_edge.sum())), 3)  # soft gradients: photographs
    # straight lines
    edges = cv2.Canny(gray, 60, 150)
    segs = cv2.HoughLinesP(edges, 1, np.pi / 180, 40, minLineLength=int(0.06 * max(gray.shape)), maxLineGap=3)
    if segs is None:
        out.update(hl_n=0, hl_len=0.0, hl_orth=0.0)
    else:
        d = np.asarray(segs, np.float32).reshape(-1, 4)
        dx, dy = d[:, 2] - d[:, 0], d[:, 3] - d[:, 1]
        ln = np.hypot(dx, dy)
        ang = np.degrees(np.arctan2(dy, dx)) % 90
        orth = (ang < 5) | (ang > 85)
        out["hl_n"] = int(len(d))
        out["hl_len"] = round(float(ln.sum() / (max(gray.shape) * 20)), 3)
        out["hl_orth"] = round(float(ln[orth].sum() / max(1e-6, ln.sum())), 3)
    # where the ink is: bounding box of the non-white content, light greys (roads, tints), horizontal and vertical line balance
    ys, xs = np.nonzero(ink)
    if len(xs) > 20:
        bw, bh = xs.max() - xs.min() + 1, ys.max() - ys.min() + 1
        out["c_aspect"] = round(float(bw) / bh, 3)
        out["c_share"] = round(float(bw * bh) / n, 3)
        out["c_fill"] = round(ink_share * n / float(bw * bh), 3)
    else:
        out.update(c_aspect=1.0, c_share=0.0, c_fill=0.0)
    out["light"] = round(float(((gray >= 200) & (gray < 245)).mean()), 4)
    if segs is not None and ln.sum() > 0:
        a0 = np.abs(np.degrees(np.arctan2(dy, dx)))
        out["hl_h"] = round(float(ln[(a0 < 5) | (a0 > 175)].sum() / ln.sum()), 3)
        out["hl_v"] = round(float(ln[np.abs(a0 - 90) < 5].sum() / ln.sum()), 3)
        hist = np.histogram(np.degrees(np.arctan2(dy, dx)) % 180, bins=18, range=(0, 180), weights=ln)[0]
        p = hist / max(1e-6, hist.sum())
        out["hl_ent"] = round(float(-(p[p > 0] * np.log2(p[p > 0])).sum()), 3)
    else:
        out.update(hl_h=0.0, hl_v=0.0, hl_ent=0.0)
    # vivid colour blobs: seating zones are a few large flat colour areas, routes and symbols are many small ones
    vm = ((hsv[:, :, 1] > 115) & (hsv[:, :, 2] > 76)).astype(np.uint8)
    nv, _, stv, _ = cv2.connectedComponentsWithStats(vm, connectivity=8)
    av = stv[1:, cv2.CC_STAT_AREA] if nv > 1 else np.zeros(0)
    out["vb_big"] = int((av > 0.004 * n).sum())
    out["vb_max"] = round(float(av.max() / n), 4) if len(av) else 0.0
    out["vb_n"] = int((av > 0.0002 * n).sum())
    # closed rooms: enclosed white areas of moderate size with box-like shapes
    dark = (gray < 170).astype(np.uint8)
    dark = cv2.dilate(dark, np.ones((2, 2), np.uint8))
    nlab, lab, st, _ = cv2.connectedComponentsWithStats(1 - dark, connectivity=4)
    areas = st[1:, cv2.CC_STAT_AREA]
    boxes = st[1:, cv2.CC_STAT_WIDTH] * st[1:, cv2.CC_STAT_HEIGHT]
    sel = (areas > 0.0003 * n) & (areas < 0.08 * n)
    out["rooms"] = int(sel.sum())
    out["room_fill"] = round(float((areas[sel] / boxes[sel]).mean()), 3) if sel.any() else 0.0
    return out


def vector_shape(page, dr):
    """Orthogonal share of vector lines, tiny repeated shapes (seats), longest dark line weights."""
    r = page.rect
    n_l = n_orth = 0
    long_l = 0
    small = 0
    area_pg = r.width * r.height
    for d in dr:
        rc = d["rect"]
        if rc.width * rc.height < 0.00015 * area_pg and rc.width > 0.2 and rc.height > 0.2:
            small += 1
        for it in d["items"]:
            if it[0] == "l":
                dx, dy = abs(it[2][0] - it[1][0]), abs(it[2][1] - it[1][1])
                n_l += 1
                if min(dx, dy) < 0.02 * max(dx, dy, 1e-6) + 0.05:
                    n_orth += 1
                if max(dx, dy) > 0.05 * max(r.width, r.height):
                    long_l += 1
    return dict(v_orth=round(n_orth / max(1, n_l), 3), v_long=long_l, v_small=small)


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
    f["text"] = re.sub(r"\s+", " ", text)[:3000]
    f["n_images"] = len(boxes)
    ba = sorted(((b[2] - b[0]) * (b[3] - b[1]) / (r.width * r.height) for b in boxes), reverse=True)
    f["img_max"] = round(float(ba[0]), 3) if ba else 0.0
    f["img_big"] = sum(1 for a in ba if a > 0.04)
    f["text_cover"] = round(coverage(r, [(w[0], w[1], w[2], w[3]) for w in words]), 3)
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
    big = pdfio.render(page, width_px=640) if r.width >= r.height else pdfio.render(page, scale=640.0 / r.height)
    f.update(raster_shape(big))
    f.update(vector_shape(page, dr))
    f["n_scale_ratio"] = len(re.findall(r"1\s?:\s?\d{2,5}", text))
    f["n_big_words"] = sum(1 for sp in page.get_text("dict")["blocks"] if sp.get("type") == 0
                           for ln in sp["lines"] for s in ln["spans"] if s["size"] > 20)
    f["dhash"] = "%064x" % dhash(rgb)
    return f

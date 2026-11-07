"""Raster plans (scans, embedded images): outline by thresholding and morphology. Units are pixels; no walls or rooms.

A scan has no vector weights to tell a cut wall from furniture, so only the outline is taken, and every result is
low confidence at best: the scale has to come from a bar (none here is machine readable) or from the footprint.
"""
import cv2
import numpy as np
from shapely.geometry import Polygon


def outline_px(gray, close_frac=0.03, min_frac=0.02):
    """Largest filled blob of the drawing's ink as a polygon in pixels, and the ink mask."""
    g = cv2.GaussianBlur(gray, (0, 0), 1.0)
    bg = cv2.medianBlur(g, 51 if min(g.shape) > 200 else 21)
    diff = bg.astype(np.int16) - g.astype(np.int16)  # ink is darker than its surroundings, even if faint
    thr = max(6, int(np.percentile(diff[diff > 0], 90) * 0.35)) if (diff > 0).any() else 6
    ink = (diff > thr).astype(np.uint8) * 255
    n, lab, st, _ = cv2.connectedComponentsWithStats(ink, connectivity=8)
    keep = np.zeros(n, bool)
    for i in range(1, n):
        if max(st[i, cv2.CC_STAT_WIDTH], st[i, cv2.CC_STAT_HEIGHT]) >= 0.04 * max(gray.shape):  # drop text and specks
            keep[i] = True
    ink = np.where(keep[lab], 255, 0).astype(np.uint8)
    k = max(3, int(close_frac * max(gray.shape))) | 1
    closed = cv2.morphologyEx(ink, cv2.MORPH_CLOSE, cv2.getStructuringElement(cv2.MORPH_RECT, (k, k)))
    cnts, _ = cv2.findContours(closed, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    if not cnts:
        return None, ink
    c = max(cnts, key=cv2.contourArea)
    if cv2.contourArea(c) < min_frac * gray.shape[0] * gray.shape[1]:
        return None, ink
    eps = 0.004 * cv2.arcLength(c, True)
    poly = cv2.approxPolyDP(c, eps, True)[:, 0, :].astype(float)
    return Polygon(poly).buffer(0), ink

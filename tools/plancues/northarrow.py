"""North arrow of a plan: a small circle with a marked half line (heavy, or ending in a filled arrowhead)."""
import math


def _circle_candidates(drawings):
    out = []
    for d in drawings:
        r = d["rect"]
        if not (6 <= r.width <= 45 and abs(r.width - r.height) < 0.15 * r.width):
            continue
        nc = sum(1 for i in d["items"] if i[0] == "c")
        if nc >= 4 and nc >= len(d["items"]) - 2:
            out.append(r)
    return out


def detect(drawings):
    """(dx, dz, method) of the page direction the arrow calls north (z down), or None."""
    for circ in _circle_candidates(drawings):
        cx, cy = (circ.x0 + circ.x1) / 2, (circ.y0 + circ.y1) / 2
        box = (circ.x0 - 2, circ.y0 - 2, circ.x1 + 2, circ.y1 + 2)
        lines, tris = [], []
        for d in drawings:
            r = d["rect"]
            if not (r.x0 >= box[0] and r.x1 <= box[2] and r.y0 >= box[1] and r.y1 <= box[3]):
                continue
            if d is None:
                continue
            ls = [(it[1], it[2]) for it in d["items"] if it[0] == "l"]
            if d.get("type") in ("f", "fs") and len(ls) == 3 and len(d["items"]) == 3:
                tris.append(ls)
            elif d.get("type") == "s" and len(ls) == 1 and len(d["items"]) == 1:
                lw = d.get("width") or 0
                lines.append((ls[0][0], ls[0][1], lw))
        if not lines:
            continue
        # (a) a clearly heavier line: north is its end away from the centre
        if len(lines) >= 2:
            lw = sorted(lines, key=lambda t: -t[2])
            if lw[0][2] > 2.5 * lw[1][2]:
                a, b, _ = lw[0]
                da, db = math.dist(a, (cx, cy)), math.dist(b, (cx, cy))
                far = b if db > da else a
                near = a if db > da else b
                if math.dist(near, (cx, cy)) < 0.3 * (circ.width):  # starts at the centre
                    v = (far[0] - near[0], far[1] - near[1])
                    return v[0], v[1], "heavy half line"
        # (b) an arrowhead: the line end nearest the triangle's centroid
        if tris:
            t = tris[0]
            pts = [p for seg in t for p in seg]
            tx = sum(p[0] for p in pts) / len(pts)
            ty = sum(p[1] for p in pts) / len(pts)
            best = None
            for a, b, _ in lines:
                for end, other in ((a, b), (b, a)):
                    dd = math.dist(end, (tx, ty))
                    if best is None or dd < best[0]:
                        best = (dd, end, other)
            if best and best[0] < 0.4 * circ.width + 3:
                v = (best[1][0] - best[2][0], best[1][1] - best[2][1])
                return v[0], v[1], "arrowhead"
    return None

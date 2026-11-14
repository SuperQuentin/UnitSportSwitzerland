"""A Python port of the game's room cut (tools/TerrainFormat/RoomStrips.Cut, no stairs, no kept bands): the pieces each room becomes. The game
refuses a room whose largest piece is under 1 m a side (InteriorValidator), so the export asks this which rooms it would refuse."""
import math

MERGE, GAP, MIN_PIECE, STEP = 0.15, 0.15, 0.2, 0.5
MIN_SIDE = 1.0  # the game wants 1 m a side (InteriorValidator)


def _spans(p, z):
    xs = []
    for i in range(len(p)):
        a, b = p[i], p[i - 1]
        if (a[1] > z) != (b[1] > z):
            xs.append(a[0] + (z - a[1]) * (b[0] - a[0]) / (b[1] - a[1]))
    xs.sort()
    return [(xs[i], xs[i + 1]) for i in range(0, len(xs) - 1, 2)]


def _angled(rooms, a, b):
    for p in rooms:
        for i in range(len(p)):
            q, r = p[i], p[i - 1]
            if max(q[1], r[1]) <= a + 1e-6 or min(q[1], r[1]) >= b - 1e-6:
                continue
            if abs(q[0] - r[0]) > 0.02:
                return True
    return False


def _core(strips, room):
    widest = []
    for _, _, spans in strips:
        c = [(s[2] - s[1], i) for i, s in enumerate(spans) if s[0] == room]
        widest.append(max(c)[1] if c else -1)
    mine = [strips[s][2][w] for s, w in enumerate(widest) if w >= 0]
    if len({(round(p[1], 3), round(p[2], 3)) for p in mine}) < 2:
        return
    best, bi, bj, blo, bhi = 0.0, -1, -1, 0.0, 0.0
    for i in range(len(strips)):
        if widest[i] < 0:
            continue
        lo, hi = -1e18, 1e18
        for j in range(i, len(strips)):
            if widest[j] < 0 or (j > i and abs(strips[j][0] - strips[j - 1][1]) > 1e-6):
                break
            p = strips[j][2][widest[j]]
            lo, hi = max(lo, p[1]), min(hi, p[2])
            if hi - lo < 1.0:
                break
            area = (hi - lo) * (strips[j][1] - strips[i][0])
            if area > best:
                best, bi, bj, blo, bhi = area, i, j, lo, hi
    if bi < 0:
        return
    for s in range(bi, bj + 1):
        spans, w = strips[s][2], widest[s]
        r, x0, x1 = spans[w]
        lo = x0 if blo - x0 < 0.3 else blo
        hi = x1 if x1 - bhi < 0.3 else bhi
        parts = [(r, a, b) for a, b in ((x0, lo), (lo, hi), (hi, x1)) if b - a > 1e-6]
        spans[w:w + 1] = parts


def cut(rooms):
    """rooms: list of point lists [(x, z), ...]. Returns {room: [(x0, z0, x1, z1), ...]}, the pieces of each."""
    lines = sorted(round(p[1], 3) for r in rooms for p in r)
    kept = []
    for z in lines:
        if not kept or z - kept[-1] >= MERGE:
            kept.append(z)
    allz = []
    for i, a in enumerate(kept):
        allz.append(a)
        if i + 1 == len(kept):
            break
        b = kept[i + 1]
        if b - a <= STEP + 0.3 or not _angled(rooms, a, b):
            continue
        n = math.ceil((b - a) / STEP)
        last = a
        for z in (a + (b - a) * k / n for k in range(1, n)):
            if a + MERGE < z < b - MERGE and z - last >= MERGE:
                allz.append(z)
                last = z
    strips = []
    for s in range(len(allz) - 1):
        za, zb = allz[s], allz[s + 1]
        spans = [(r, x0, x1) for r, p in enumerate(rooms) for x0, x1 in _spans(p, (za + zb) / 2)]
        spans.sort(key=lambda t: t[1])
        for i in range(len(spans) - 1):
            ra, a0, a1 = spans[i]
            rb, b0, b1 = spans[i + 1]
            if ra == rb or b0 - a1 > GAP:
                continue
            mid = min(max((a1 + b0) / 2, a0), b1)
            spans[i], spans[i + 1] = (ra, a0, mid), (rb, mid, b1)
        strips.append((za, zb, spans))
    for r in range(len(rooms)):
        _core(strips, r)
    pieces, opened = [], {}
    for za, zb, spans in strips:
        nxt = {}
        for r, x0, x1 in spans:
            if x1 - x0 < MIN_PIECE or zb - za < 1e-6:
                continue
            key = (r, round(x0 * 1000), round(x1 * 1000))
            at = opened.get(key)
            if at is not None and abs(pieces[at][3] - za) < 1e-6:
                pieces[at] = pieces[at][:3] + (zb, r)
                nxt[key] = at
            else:
                pieces.append((x0, za, x1, zb, r))
                nxt[key] = len(pieces) - 1
        opened = nxt
    out = {}
    for p in pieces:
        if p[3] - p[1] >= MIN_PIECE / 2:
            out.setdefault(p[4], []).append(p[:4])
    return out


def thin_rooms(rooms):
    """Indices of the rooms whose largest piece is under 1 m a side, with that piece's (width, depth); the game refuses them."""
    pieces = cut(rooms)
    bad = {}
    for r in range(len(rooms)):
        ps = pieces.get(r, [])
        if not ps:
            bad[r] = (0.0, 0.0)
            continue
        m = max(ps, key=lambda q: (q[2] - q[0]) * (q[3] - q[1]))
        if min(m[2] - m[0], m[3] - m[1]) < MIN_SIDE:
            bad[r] = (m[2] - m[0], m[3] - m[1])
    return bad

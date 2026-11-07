"""Readers for the game's .bldg and .cover tiles (formats: tools/TerrainFormat BuildingCodec, CoverFormat)."""
import os
import struct
import zlib

import numpy as np

SPORTS_FIELD = 30  # CoverClass.SportsField
COVER_SIZE = 1001  # vertices per side, 1 m apart


def tile_of(e, n):
    return int(e // 1000), int(n // 1000)


def read_buildings(chunks, tile):
    """Buildings of a tile: list of dicts (kind, floors, egid, min_y, max_y, tris (k,3,3) float32 tile-local x,y,z)."""
    path = os.path.join(chunks, "buildings_%d_%d.bldg" % tile)
    if not os.path.exists(path):
        return []
    with open(path, "rb") as f:
        data = f.read()
    magic, version = struct.unpack_from("<IH", data, 0)
    if magic != 0x44425355:
        raise ValueError("bad building magic in " + path)
    count = struct.unpack_from("<I", data, 16)[0]
    pos, out = 24, []
    for _ in range(count):
        kind, floors, year, egid, miny, maxy, ntri = struct.unpack_from("<BBHIffI", data, pos)
        pos += 20
        tris = np.frombuffer(data, dtype="<f4", count=ntri * 9, offset=pos).reshape(ntri, 3, 3)
        pos += ntri * 36
        out.append(dict(kind=kind, floors=floors, egid=egid, min_y=miny, max_y=maxy, tris=tris, tile=tile))
    return out


def footprint_polygons(b):
    """The solid's plan footprint as a shapely geometry in LV95 (union of its non-vertical triangles)."""
    from shapely.geometry import Polygon
    from shapely.ops import unary_union
    e0, n1 = b["tile"][0] * 1000.0, (b["tile"][1] + 1) * 1000.0
    polys = []
    for t in b["tris"]:
        a = (t[:, 0], t[:, 2])
        area = (t[1, 0] - t[0, 0]) * (t[2, 2] - t[0, 2]) - (t[1, 2] - t[0, 2]) * (t[2, 0] - t[0, 0])
        if abs(area) < 0.01:
            continue
        p = Polygon([(e0 + x, n1 - z) for x, z in zip(*a)])
        if p.is_valid and p.area > 0:
            polys.append(p)
    return unary_union(polys).buffer(0.02).buffer(-0.02)


def read_cover(chunks, tile):
    """Cover classes of a tile as a (1001, 1001) uint8 array [row = z south, col = x east]; None when absent."""
    path = os.path.join(chunks, "cover_%d_%d.cover" % tile)
    if not os.path.exists(path):
        return None
    with open(path, "rb") as f:
        data = f.read()
    raw = zlib.decompress(data[20:], -15)
    return np.frombuffer(raw, dtype=np.uint8, count=COVER_SIZE * COVER_SIZE).reshape(COVER_SIZE, COVER_SIZE)


def sports_field_cells(chunks, tile):
    """LV95 (e, n) of every SportsField vertex of a tile, (k, 2) float."""
    c = read_cover(chunks, tile)
    if c is None:
        return np.zeros((0, 2))
    r, q = np.nonzero(c == SPORTS_FIELD)
    return np.stack([tile[0] * 1000.0 + q, (tile[1] + 1) * 1000.0 - r], axis=1)

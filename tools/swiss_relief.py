"""Builds src/Terrain/swiss_relief.gz: a coarse heightmap of Switzerland and its borders
that the generated terrain (src/Terrain/ProceduralWorld) is shaped on.

Source: swisstopo swissALTIRegio (open data, (c) swisstopo), a 10 m DTM of Switzerland,
Liechtenstein and the neighbouring border areas in LV95, published as one 10 GB cloud-optimised
GeoTIFF. Only one of its overviews is read, over HTTP range requests (a few MB), and averaged
down to a 500 m grid, so this needs GDAL's Python bindings but no download of the file.

Nodes sit on LV95 multiples of the spacing; each is the mean of the cell centred on it. Holes
(areas the source does not cover) are filled by repeated averaging of their known neighbours,
so the grid is complete and smooth into them.

Format (gzip): "SWRL", u16 version (1), u16 reserved, i32 minE, i32 maxN, i32 spacing,
i32 cols, i32 rows, then rows x cols u16 heights in decimetres, row 0 the northernmost,
little-endian.

Usage:
    python tools/swiss_relief.py [--spacing 500] [--out src/Terrain/swiss_relief.gz]
"""
import argparse
import array
import gzip
import os
import struct
import sys

from osgeo import gdal

gdal.UseExceptions()

SOURCE = ("/vsicurl/https://data.geo.admin.ch/ch.swisstopo.swissaltiregio/"
          "swissaltiregio/swissaltiregio_2056_5728.tif")
# the source's extent, LV95
MIN_E, MAX_E, MIN_N, MAX_N = 2385000, 2935000, 974000, 1404000
HERE = os.path.dirname(os.path.abspath(__file__))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--spacing", type=int, default=500)
    ap.add_argument("--out", default=os.path.join(HERE, "..", "src", "Terrain", "swiss_relief.gz"))
    args = ap.parse_args()
    s = args.spacing

    gdal.SetConfigOption("GDAL_DISABLE_READDIR_ON_OPEN", "EMPTY_DIR")
    gdal.SetConfigOption("GDAL_HTTP_MULTIRANGE", "YES")
    cols = (MAX_E - MIN_E) // s + 1
    rows = (MAX_N - MIN_N) // s + 1
    print(f"warping to {cols} x {rows} at {s} m ...", flush=True)
    # cells centred on the nodes; GDAL picks the overview just finer than the target itself
    ds = gdal.Warp("", SOURCE, format="MEM",
                   outputBounds=(MIN_E - s / 2, MIN_N - s / 2, MAX_E + s / 2, MAX_N + s / 2),
                   xRes=s, yRes=s, resampleAlg="average", dstNodata=-9999, outputType=gdal.GDT_Float32)
    band = ds.GetRasterBand(1)
    raw = band.ReadRaster(0, 0, cols, rows, buf_type=gdal.GDT_Float32)
    h = array.array("f")
    h.frombytes(raw)
    if sys.byteorder != "little":
        h.byteswap()

    known = [v > -1000 for v in h]
    print(f"{sum(known)} of {len(h)} nodes have data", flush=True)
    fill_holes(h, known, cols, rows)

    out = array.array("H", (max(0, min(65535, round(v * 10))) for v in h))
    if sys.byteorder != "little":
        out.byteswap()
    header = b"SWRL" + struct.pack("<HHiiiii", 1, 0, MIN_E, MAX_N, s, cols, rows)
    with gzip.open(args.out, "wb", compresslevel=9) as f:
        f.write(header)
        f.write(out.tobytes())
    print(f"wrote {args.out}: {os.path.getsize(args.out)} bytes, "
          f"{min(h):.0f}..{max(h):.0f} m")


def fill_holes(h, known, cols, rows):
    """Pull-push: each unknown node takes the mean of its known neighbours, a ring at a time,
    then a few smoothing passes over the filled nodes only, so the data is never altered."""
    filled = list(known)
    frontier = True
    passes = 0
    while frontier:
        frontier = False
        new = []
        for r in range(rows):
            for c in range(cols):
                k = r * cols + c
                if filled[k]:
                    continue
                total = n = 0
                for dr in (-1, 0, 1):
                    for dc in (-1, 0, 1):
                        rr, cc = r + dr, c + dc
                        if 0 <= rr < rows and 0 <= cc < cols and filled[rr * cols + cc]:
                            total += h[rr * cols + cc]
                            n += 1
                if n:
                    new.append((k, total / n))
        for k, v in new:
            h[k] = v
            filled[k] = True
            frontier = True
        passes += 1
    for _ in range(20):
        for r in range(1, rows - 1):
            for c in range(1, cols - 1):
                k = r * cols + c
                if not known[k]:
                    h[k] = (h[k - 1] + h[k + 1] + h[k - cols] + h[k + cols]) / 4
    print(f"filled holes in {passes} rings", flush=True)


if __name__ == "__main__":
    main()

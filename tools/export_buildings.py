"""Converts swissBUILDINGS3D 3.0 (FileGDB, TIN solids) into a GeoPackage the C#
preprocessor can read without GDAL.

This is a pure format conversion — all game-specific work (tile assignment, cadastre
join, mesh building) stays in the C# pipeline so the binary formats have one owner.

Usage:
    python tools/export_buildings.py --bbox 2579000 1109000 2586000 1115000
    python tools/export_buildings.py            # whole country (slow, large)
    python tools/export_buildings.py --src "ressources/data/buildings3d/swissbuildings3d_3_0_*_1305-*.gdb.zip"
                                                # per-sheet downloads (swiss_data.py --tiles-file)
"""
import argparse
import glob
import os
import zipfile

from osgeo import gdal, ogr

gdal.UseExceptions()
ogr.UseExceptions()

HERE = os.path.dirname(os.path.abspath(__file__))
SRC_DIR = os.path.join(HERE, "..", "ressources", "data", "buildings3d")
SRC_ZIP = "swissbuildings3d_3_0_2026_2056_5728.gdb.zip"
OUT = os.path.join(SRC_DIR, "buildings.gpkg")

# kept lean on purpose: everything else is either empty in the 3.0 Beta or unused
FIELDS = ["OBJEKTART", "DACH_MAX", "DACH_MIN", "GEBAEUDE_NUTZUNG", "NAME_KOMPLETT"]


def gdb_path(full=None):
    full = full or os.path.join(SRC_DIR, SRC_ZIP)
    root = sorted({n.split("/")[0] for n in zipfile.ZipFile(full).namelist()})[0]
    return f"/vsizip/{full}/{root}"


def expand_sources(patterns):
    """--src values: zip paths or globs. Sorted and de-duplicated so re-runs are reproducible."""
    found = set()
    for pattern in patterns:
        matches = glob.glob(pattern)
        if not matches:
            raise SystemExit(f"error: --src {pattern} matches nothing")
        found.update(os.path.abspath(m) for m in matches)
    return sorted(found)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--bbox", nargs=4, type=float, metavar=("MINE", "MINN", "MAXE", "MAXN"))
    ap.add_argument("--out", default=OUT)
    ap.add_argument("--src", action="append", default=[], metavar="ZIP_OR_GLOB",
                    help="per-sheet .gdb.zip file(s) to convert instead of the nationwide one; repeatable, "
                         "globs allowed. Merged into one GeoPackage.")
    args = ap.parse_args()

    sources = [gdb_path(z) for z in expand_sources(args.src)] if args.src else [gdb_path()]
    if os.path.exists(args.out):
        os.remove(args.out)

    for i, src in enumerate(sources):
        print(f"source {i + 1}/{len(sources)}: {src}", flush=True)
        translate(src, args.out, args.bbox, append=i > 0)
    report(args.out)


def translate(src, out, bbox, append):
    if append:
        # GDAL refuses -select together with -append, so a later sheet is first converted on its
        # own (same fields, same layer) and that already-trimmed layer is what gets appended
        part = out + ".part.gpkg"
        if os.path.exists(part):
            os.remove(part)
        translate(src, part, bbox, append=False)
        gdal.VectorTranslate(out, part, options=gdal.VectorTranslateOptions(
            format="GPKG", accessMode="append", layers=["buildings"], layerName="buildings"))
        os.remove(part)
        return
    opts = gdal.VectorTranslateOptions(
        format="GPKG",
        layers=["Building_solid"],
        layerName="buildings",
        selectFields=FIELDS,
        # TIN is not a GeoPackage geometry type; MultiPolygonZ keeps every triangle
        # and the per-face structure we need to build meshes.
        geometryType="MULTIPOLYGON25D",
        spatFilter=bbox if bbox else None,
        spatSRS="EPSG:2056",
        makeValid=False,
    )
    gdal.VectorTranslate(out, src, options=opts)


def report(out):
    ds = ogr.Open(out)
    layer = ds.GetLayerByName("buildings")
    n = layer.GetFeatureCount()
    print(f"wrote {out}: {n} buildings, {os.path.getsize(out)/1e6:.1f} MB")
    if n:
        f = layer.GetNextFeature()
        print("  sample geom:", f.GetGeometryRef().GetGeometryName(),
              "parts:", f.GetGeometryRef().GetGeometryCount())


if __name__ == "__main__":
    main()

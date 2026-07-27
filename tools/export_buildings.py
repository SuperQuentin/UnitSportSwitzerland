"""Converts swissBUILDINGS3D 3.0 (FileGDB, TIN solids) into a GeoPackage the C#
preprocessor can read without GDAL.

This is a pure format conversion — all game-specific work (tile assignment, cadastre
join, mesh building) stays in the C# pipeline so the binary formats have one owner.

Usage:
    python tools/export_buildings.py --bbox 2579000 1109000 2586000 1115000
    python tools/export_buildings.py            # whole country (slow, large)
"""
import argparse
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


def gdb_path():
    full = os.path.join(SRC_DIR, SRC_ZIP)
    root = sorted({n.split("/")[0] for n in zipfile.ZipFile(full).namelist()})[0]
    return f"/vsizip/{full}/{root}"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--bbox", nargs=4, type=float, metavar=("MINE", "MINN", "MAXE", "MAXN"))
    ap.add_argument("--out", default=OUT)
    args = ap.parse_args()

    src = gdb_path()
    print(f"source: {src}")
    if os.path.exists(args.out):
        os.remove(args.out)

    opts = gdal.VectorTranslateOptions(
        format="GPKG",
        layers=["Building_solid"],
        layerName="buildings",
        selectFields=FIELDS,
        # TIN is not a GeoPackage geometry type; MultiPolygonZ keeps every triangle
        # and the per-face structure we need to build meshes.
        geometryType="MULTIPOLYGON25D",
        spatFilter=args.bbox if args.bbox else None,
        spatSRS="EPSG:2056",
        makeValid=False,
    )
    gdal.VectorTranslate(args.out, src, options=opts)

    ds = ogr.Open(args.out)
    layer = ds.GetLayerByName("buildings")
    n = layer.GetFeatureCount()
    print(f"wrote {args.out}: {n} buildings, {os.path.getsize(args.out)/1e6:.1f} MB")
    if n:
        f = layer.GetNextFeature()
        print("  sample geom:", f.GetGeometryRef().GetGeometryName(),
              "parts:", f.GetGeometryRef().GetGeometryCount())


if __name__ == "__main__":
    main()

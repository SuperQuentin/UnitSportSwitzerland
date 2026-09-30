"""One-off: export the TLM3D road UUIDs that belong to the national cycling /
mountain-biking networks, so the C# preprocessor can flag them without needing GDAL.

The ASTRA Veloland / Mountainbikeland FileGDBs carry a TLM_ID field referencing
swissTLM3D road segments, so this is a plain key export -- no spatial matching.

Usage:  python tools/export_route_keys.py [--dir DIR]
Output: DIR/route_keys.sqlite  (table route_key: uuid, kind); DIR defaults to ressources/data/routes
"""
import argparse
import os
import sqlite3
import zipfile

from osgeo import ogr

ogr.UseExceptions()

HERE = os.path.dirname(os.path.abspath(__file__))
ROUTES = os.path.join(HERE, "..", "ressources", "data", "routes")

SOURCES = [
    ("veloland_2056.gdb.zip", "VeloWeg", "cycle"),
    ("mountainbikeland_2056.gdb.zip", "MTBWeg", "mtb"),
]


def gdb_path(full):
    root = sorted({n.split("/")[0] for n in zipfile.ZipFile(full).namelist()})[0]
    return f"/vsizip/{full}/{root}"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", default=ROUTES, help="folder holding the downloaded route zips; the output goes there too")
    args = ap.parse_args()
    routes = os.path.abspath(args.dir)
    os.makedirs(routes, exist_ok=True)
    out = os.path.join(routes, "route_keys.sqlite")
    if os.path.exists(out):
        os.remove(out)
    db = sqlite3.connect(out)
    db.execute("create table route_key (uuid text not null, kind text not null)")

    for zip_name, layer_name, kind in SOURCES:
        path = os.path.join(routes, zip_name)
        if not os.path.exists(path):
            print(f"  skip {zip_name} (not downloaded)")
            continue
        # keep the DataSource referenced: if it is collected the layer becomes invalid
        ds = ogr.Open(gdb_path(path))
        layer = ds.GetLayerByName(layer_name)
        seen = set()
        for feat in layer:
            tlm_id = feat.GetField("TLM_ID")
            if tlm_id:
                seen.add(tlm_id)
        db.executemany(
            "insert into route_key (uuid, kind) values (?, ?)",
            ((u, kind) for u in sorted(seen)),
        )
        print(f"  {kind}: {len(seen)} distinct TLM road segments")

    db.execute("create index idx_route_key_uuid on route_key (uuid)")
    db.commit()
    print(f"wrote {out}")


if __name__ == "__main__":
    main()

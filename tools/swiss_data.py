"""Downloads the Swiss open-data assets this project consumes (swissALTI3D,
swissTLM3D, swissBUILDINGS3D via the swisstopo STAC API, GWR/RegBL per canton, and
the ASTRA Veloland/Mountainbikeland route networks), replacing the manual `curl`
workflow the data under ressources/data/ was originally fetched with.

Re-runs are cheap: every asset is HEAD-checked against a small per-directory
manifest (size + ETag/Last-Modified) before downloading, so files that haven't
changed on the server are skipped instead of re-fetched.

Usage:
    python tools/swiss_data.py --list
    python tools/swiss_data.py swissalti3d --bbox 2579000 1109000 2586000 1115000
    python tools/swiss_data.py swisstlm3d
    python tools/swiss_data.py swissbuildings3d --bbox 2579000 1109000 2586000 1115000
    python tools/swiss_data.py gwr --canton vs
    python tools/swiss_data.py veloland
    python tools/swiss_data.py --dry-run swissalti3d --bbox 2579000 1109000 2586000 1115000
"""
import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
DATA_ROOT = os.path.join(HERE, "..", "ressources", "data")
STAC_BASE = "https://data.geo.admin.ch/api/stac/v0.9"
DISK_HEADROOM = 1.10  # require 10% more free space than the download total
# opendata.swiss (CKAN) 403s any request with no User-Agent at all; harmless
# elsewhere, so it's sent on every request rather than special-cased.
USER_AGENT = "UnitSportSwitzerland-swiss_data.py"

# ---------------------------------------------------------------------------
# LV95 <-> WGS84 (swisstopo's approximate formulas; ported from
# tools/TerrainFormat/SwissProjection.cs -- accurate to well under a metre
# inside Switzerland, which is all the STAC bbox query needs).
# ---------------------------------------------------------------------------


def lv95_to_wgs84(e, n):
    y = (e - 2600000.0) / 1000000.0
    x = (n - 1200000.0) / 1000000.0

    lon = (2.6779094 + 4.728982 * y + 0.791484 * y * x + 0.1306 * y * x * x
           - 0.0436 * y * y * y)
    lat = (16.9023892 + 3.238272 * x - 0.270978 * y * y - 0.002528 * x * x
           - 0.0447 * y * y * x - 0.0140 * x * x * x)

    return lat * 100.0 / 36.0, lon * 100.0 / 36.0


def bbox_lv95_to_wgs84(min_e, min_n, max_e, max_n):
    lat1, lon1 = lv95_to_wgs84(min_e, min_n)
    lat2, lon2 = lv95_to_wgs84(max_e, max_n)
    return min(lon1, lon2), min(lat1, lat2), max(lon1, lon2), max(lat1, lat2)


# ---------------------------------------------------------------------------
# HTTP helpers. `urllib` is tried first; a conda-updated openssl is known to
# break its HTTPS handshake on this project's dev machine ("ASN1:
# NOT_ENOUGH_DATA"), in which case we fall back to `curl`, which has always
# worked here (see swiss-geodata-sources memory).
# ---------------------------------------------------------------------------


def _curl(args, capture=True):
    result = subprocess.run(["curl", "-sSL", "-A", USER_AGENT] + args, capture_output=capture)
    if result.returncode != 0:
        raise RuntimeError(f"curl failed ({result.returncode}): {' '.join(args)}")
    return result


def _request(url, method="GET"):
    return urllib.request.Request(url, method=method, headers={"User-Agent": USER_AGENT})


def http_get_json(url):
    try:
        with urllib.request.urlopen(_request(url), timeout=60) as resp:
            return json.load(resp)
    except (urllib.error.URLError, ConnectionError, OSError):
        return json.loads(_curl([url]).stdout)


def http_head(url):
    """Returns {size, etag, last_modified} for url, or None if it 404s."""
    try:
        with urllib.request.urlopen(_request(url, method="HEAD"), timeout=30) as resp:
            headers = resp.headers
    except urllib.error.HTTPError as e:
        if e.code == 404:
            return None
        headers = None
    except (urllib.error.URLError, ConnectionError, OSError):
        headers = None

    if headers is None:
        result = subprocess.run(["curl", "-sSI", "-L", "-A", USER_AGENT, url],
                                 capture_output=True, text=True)
        if result.returncode != 0:
            return None
        headers = {}
        for line in result.stdout.splitlines():
            if ":" in line:
                k, _, v = line.partition(":")
                headers[k.strip()] = v.strip()
        if "content-length" not in {k.lower() for k in headers}:
            return None
        get = lambda name: next((v for k, v in headers.items() if k.lower() == name), None)
    else:
        get = headers.get

    size = get("Content-Length") or get("content-length")
    return {
        "size": int(size) if size else None,
        "etag": get("ETag") or get("etag"),
        "last_modified": get("Last-Modified") or get("last-modified"),
    }


def download_file(url, dest):
    tmp = dest + ".part"
    try:
        with urllib.request.urlopen(_request(url), timeout=60) as resp, open(tmp, "wb") as f:
            shutil.copyfileobj(resp, f)
    except (urllib.error.URLError, ConnectionError, OSError):
        _curl(["-o", tmp, url], capture=False)
    os.replace(tmp, dest)


# ---------------------------------------------------------------------------
# Per-directory change-detection manifest.
# ---------------------------------------------------------------------------


def manifest_path(out_dir):
    return os.path.join(out_dir, ".swiss_data_manifest.json")


def load_manifest(out_dir):
    path = manifest_path(out_dir)
    if os.path.exists(path):
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    return {}


def save_manifest(out_dir, manifest):
    with open(manifest_path(out_dir), "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2)


def is_up_to_date(dest, entry, head):
    if entry is None or not os.path.exists(dest):
        return False
    if os.path.getsize(dest) != entry.get("size"):
        return False
    if head.get("size") is not None and head["size"] != entry.get("size"):
        return False
    if head.get("etag") and entry.get("etag") and head["etag"] != entry.get("etag"):
        return False
    if (head.get("last_modified") and entry.get("last_modified")
            and head["last_modified"] != entry.get("last_modified")):
        return False
    return True


# ---------------------------------------------------------------------------
# STAC item/asset iteration.
# ---------------------------------------------------------------------------


def stac_items(collection, bbox_wgs84=None):
    params = {"limit": "100"}
    if bbox_wgs84:
        params["bbox"] = ",".join(f"{v:.6f}" for v in bbox_wgs84)
    url = f"{STAC_BASE}/collections/{collection}/items?" + urllib.parse.urlencode(params)
    while url:
        page = http_get_json(url)
        for feature in page.get("features", []):
            yield feature
        url = next((link["href"] for link in page.get("links", []) if link.get("rel") == "next"), None)


def bbox_span(feature):
    b = feature.get("bbox") or [0, 0, 0, 0]
    return max(b[2] - b[0], b[3] - b[1])


# ---------------------------------------------------------------------------
# Dataset resolvers: each yields (url, filename) pairs to fetch into a subdir.
# ---------------------------------------------------------------------------


def resolve_swissalti3d(args):
    bbox = bbox_lv95_to_wgs84(*args.bbox) if args.bbox else None
    pattern = re.compile(rf"_{re.escape(args.res)}_2056_5728\.xyz\.zip$")

    # swisstopo re-flies tiles over the years and keeps every past flight as a
    # separate STAC item with the same tile id, so a plain bbox query returns
    # 2019 AND 2024 versions of the same 1km square -- keep only the newest.
    id_re = re.compile(r"^swissalti3d_(\d{4})_(\d+-\d+)$")
    latest_by_tile = {}
    for feature in stac_items("ch.swisstopo.swissalti3d", bbox):
        m = id_re.match(feature.get("id", ""))
        if not m:
            continue
        year, tile = m.group(1), m.group(2)
        if tile not in latest_by_tile or year > latest_by_tile[tile][0]:
            latest_by_tile[tile] = (year, feature)

    for _year, feature in latest_by_tile.values():
        for key, asset in feature.get("assets", {}).items():
            if pattern.search(key):
                yield asset["href"], key


def resolve_swisstlm3d(args):
    bbox = bbox_lv95_to_wgs84(*args.bbox) if args.bbox else None
    features = list(stac_items("ch.swisstopo.swisstlm3d", bbox))
    if not features:
        return
    latest = max(features, key=lambda f: f.get("properties", {}).get("datetime", ""))
    pattern = re.compile(r"\.gpkg\.zip$")
    assets = latest.get("assets", {})
    matches = {k: a for k, a in assets.items() if pattern.search(k)}
    if not matches:
        matches = {k: a for k, a in assets.items() if k.endswith(".gdb.zip")}
    for key, asset in matches.items():
        yield asset["href"], key


def resolve_swissbuildings3d(args):
    bbox = bbox_lv95_to_wgs84(*args.bbox) if args.bbox else None
    features = list(stac_items("ch.swisstopo.swissbuildings3d_3_0", bbox))
    if not features:
        return
    # The collection mixes per-tile items (re-flown across years, same tile
    # id) with one nationwide asset; tell them apart by how much of the
    # country the item's own bbox covers.
    features.sort(key=bbox_span, reverse=args.nationwide)
    wanted = [features[0]] if args.nationwide else [f for f in features if bbox_span(f) < 0.5]

    id_re = re.compile(r"^swissbuildings3d_3_0_(\d{4})_(\S+)$")
    latest_by_tile = {}
    for feature in wanted:
        m = id_re.match(feature.get("id", ""))
        key = m.group(2) if m else feature.get("id", "")
        year = m.group(1) if m else ""
        if key not in latest_by_tile or year > latest_by_tile[key][0]:
            latest_by_tile[key] = (year, feature)

    pattern = re.compile(r"\.gdb\.zip$")
    for _year, feature in latest_by_tile.values():
        for key, asset in feature.get("assets", {}).items():
            if pattern.search(key):
                yield asset["href"], key


def resolve_gwr(args):
    url = f"https://public.madd.bfs.admin.ch/{args.canton}.zip"
    yield url, f"gwr_{args.canton}.zip"


def _resolve_stac_gdb(collection):
    """ASTRA's route networks: one STAC item, asset key already matches the
    local filename convention (e.g. veloland_2056.gdb.zip)."""
    def resolver(args):
        pattern = re.compile(r"_2056\.gdb\.zip$")
        for feature in stac_items(collection):
            for key, asset in feature.get("assets", {}).items():
                if pattern.search(key):
                    yield asset["href"], key
    return resolver


DATASETS = {
    "swissalti3d": {
        "subdir": "swiss_chunks",
        "resolve": resolve_swissalti3d,
        "help": "swissALTI3D terrain tiles (STAC, bbox-tiled, --res 0.5|2)",
        "needs_bbox": True,
    },
    "swisstlm3d": {
        "subdir": "tlm3d",
        "resolve": resolve_swisstlm3d,
        "help": "swissTLM3D landscape model, latest release (STAC, nationwide)",
        "needs_bbox": False,
    },
    "swissbuildings3d": {
        "subdir": "buildings3d",
        "resolve": resolve_swissbuildings3d,
        "help": "swissBUILDINGS3D 3.0 LoD2 solids (STAC, bbox-tiled or --nationwide)",
        "needs_bbox": True,
    },
    "gwr": {
        "subdir": "gwr",
        "resolve": resolve_gwr,
        "help": "GWR/RegBL building cadastre, one canton per run (--canton vs)",
        "needs_bbox": False,
    },
    "veloland": {
        "subdir": "routes",
        "resolve": _resolve_stac_gdb("ch.astra.veloland"),
        "help": "ASTRA Veloland cycling network (STAC, nationwide)",
        "needs_bbox": False,
    },
    "mountainbikeland": {
        "subdir": "routes",
        "resolve": _resolve_stac_gdb("ch.astra.mountainbikeland"),
        "help": "ASTRA Mountainbikeland network (STAC, nationwide)",
        "needs_bbox": False,
    },
}


# ---------------------------------------------------------------------------
# Driver.
# ---------------------------------------------------------------------------


def run(args):
    spec = DATASETS[args.dataset]
    out_dir = os.path.join(DATA_ROOT, spec["subdir"])
    os.makedirs(out_dir, exist_ok=True)
    manifest = load_manifest(out_dir)

    print(f"resolving {args.dataset} assets...")
    plan = []  # (url, filename, dest, head)
    for url, filename in spec["resolve"](args):
        dest = os.path.join(out_dir, filename)
        head = http_head(url)
        if head is None:
            print(f"  ! could not reach {url}, skipping")
            continue
        if not args.force and is_up_to_date(dest, manifest.get(filename), head):
            print(f"  = {filename} (unchanged, {head['size'] / 1e6:.1f} MB)")
            continue
        plan.append((url, filename, dest, head))

    if not plan:
        print("nothing to download.")
        return

    total = sum(h["size"] or 0 for _, _, _, h in plan)
    print(f"{len(plan)} file(s) to fetch, {total / 1e9:.2f} GB total")

    if args.dry_run:
        for url, filename, _, head in plan:
            size = f"{head['size'] / 1e6:.1f} MB" if head["size"] else "unknown size"
            print(f"  {filename}  ({size})  {url}")
        return

    free = shutil.disk_usage(out_dir).free
    if total * DISK_HEADROOM > free:
        print(f"error: need ~{total * DISK_HEADROOM / 1e9:.2f} GB free, "
              f"only {free / 1e9:.2f} GB available on the drive holding {out_dir}",
              file=sys.stderr)
        sys.exit(1)

    for url, filename, dest, head in plan:
        print(f"  > {filename} ({(head['size'] or 0) / 1e6:.1f} MB)")
        download_file(url, dest)
        manifest[filename] = {
            "url": url,
            "size": os.path.getsize(dest),
            "etag": head.get("etag"),
            "last_modified": head.get("last_modified"),
            "downloaded_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        }
        save_manifest(out_dir, manifest)

    print(f"done: {len(plan)} file(s) written to {out_dir}")


def build_parser():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--list", action="store_true", help="list available datasets and exit")
    ap.add_argument("--dry-run", action="store_true", help="print what would be downloaded, then exit")
    ap.add_argument("--force", action="store_true", help="re-download even if the manifest says up to date")

    sub = ap.add_subparsers(dest="dataset")
    for name, spec in DATASETS.items():
        p = sub.add_parser(name, help=spec["help"])
        if spec["needs_bbox"]:
            p.add_argument("--bbox", nargs=4, type=float, required=True,
                            metavar=("MINE", "MINN", "MAXE", "MAXN"),
                            help="LV95 (EPSG:2056) bounding box")
        else:
            p.add_argument("--bbox", nargs=4, type=float, default=None,
                            metavar=("MINE", "MINN", "MAXE", "MAXN"),
                            help="LV95 (EPSG:2056) bounding box (optional, dataset is nationwide)")
        if name == "swissalti3d":
            p.add_argument("--res", choices=["0.5", "2"], default="0.5", help="grid resolution in metres")
        if name == "swissbuildings3d":
            p.add_argument("--nationwide", action="store_true",
                            help="fetch the single nationwide asset instead of per-tile ones")
        if name == "gwr":
            p.add_argument("--canton", required=True, help="canton code (vs, ge, ...) or 'ch' for the whole country")

    return ap


def main():
    ap = build_parser()
    args = ap.parse_args()

    if args.list or not args.dataset:
        print("available datasets:")
        for name, spec in DATASETS.items():
            print(f"  {name:<18} {spec['help']}")
        return

    run(args)


if __name__ == "__main__":
    main()

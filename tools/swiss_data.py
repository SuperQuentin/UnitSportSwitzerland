"""Downloads the Swiss open-data assets this project consumes (swissALTI3D,
swissTLM3D, swissBUILDINGS3D via the swisstopo STAC API, GWR/RegBL per canton, and
the ASTRA Veloland/Mountainbikeland route networks), replacing the manual `curl`
workflow the data under ressources/data/ was originally fetched with.

Re-runs are cheap: every asset is HEAD-checked against a small per-directory
manifest (size + ETag/Last-Modified) before downloading, so files that haven't
changed on the server are skipped instead of re-fetched. A file already on disk
from before this tool existed (no manifest entry yet) is also skipped if its
size already matches the server's -- it doesn't need to have been downloaded
BY this tool to count as "already own it", just to already be correct.

Usage:
    python tools/swiss_data.py --list
    python tools/swiss_data.py swissalti3d --bbox 2579000 1109000 2586000 1115000
    python tools/swiss_data.py swisstlm3d
    python tools/swiss_data.py swissbuildings3d --bbox 2579000 1109000 2586000 1115000
    python tools/swiss_data.py gwr --canton vs
    python tools/swiss_data.py veloland
    python tools/swiss_data.py osm          # OpenStreetMap extract (ODbL), newest dated Geofabrik file
    python tools/swiss_data.py --dry-run swissalti3d --bbox 2579000 1109000 2586000 1115000
    python tools/swiss_data.py swissalti3d --tiles-file my_tiles.txt       # "2583-1113" per line
    python tools/swiss_data.py --out D:/swissalti3d swissalti3d --bbox 2485000 1075000 2834000 1296000
    python tools/swiss_data.py --out E:/alti --fill-disk swissalti3d --bbox 2485000 1075000 2834000 1296000

Speed: downloads run in parallel (--jobs, default 8) over keep-alive connections, files of
256 MB or more are fetched as parallel byte ranges, and an interrupted download resumes from
its .part file. Every file is SHA-256 checked against swisstopo's published checksum as it
streams in; once a file is in the manifest with that checksum, later runs know it is current
without asking the server anything.

--fill-disk downloads whatever fits, leaving --reserve-mb (default 500) free, instead of
refusing when the whole set does not. Tiles go nearest-to-the-bbox-centre first, so a disk
that fills up holds one contiguous area rather than a scatter.
"""
import argparse
import concurrent.futures
import errno
import hashlib
import http.client
import json
import os
import re
import shutil
import ssl
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
DATA_ROOT = os.path.join(HERE, "..", "ressources", "data")
STAC_BASE = "https://data.geo.admin.ch/api/stac/v0.9"
DISK_HEADROOM = 1.10  # require 10% more free space than the download total
# A nationwide listing can be tens of thousands of files; checking them one at a
# time (as the first version of this tool did) looked hung for many minutes with
# no output. Concurrency is what makes a HEAD check per file tractable at that
# scale -- kept modest so as not to hammer swisstopo's server.
HEAD_CHECK_WORKERS = 32
STAC_WORKERS = 8
PROGRESS_EVERY = 200
DOWNLOAD_WORKERS = 8
CHUNK = 1 << 20
# one file this big is split into byte ranges fetched in parallel (TLM, nationwide buildings)
SEGMENT_MIN_BYTES = 256 << 20
SEGMENT_BYTES = 64 << 20
MAX_SEGMENTS = 8
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


def wgs84_to_lv95(lat, lon):
    phi = (lat * 3600.0 - 169028.66) / 10000.0
    lam = (lon * 3600.0 - 26782.5) / 10000.0
    e = (2600072.37 + 211455.93 * lam - 10938.51 * lam * phi - 0.36 * lam * phi * phi
         - 44.54 * lam * lam * lam)
    n = (1200147.07 + 308807.95 * phi + 3745.25 * lam * lam + 76.63 * phi * phi
         - 194.56 * lam * lam * phi + 119.79 * phi * phi * phi)
    return e, n


def bbox_lv95_to_wgs84(min_e, min_n, max_e, max_n):
    lat1, lon1 = lv95_to_wgs84(min_e, min_n)
    lat2, lon2 = lv95_to_wgs84(max_e, max_n)
    return min(lon1, lon2), min(lat1, lat2), max(lon1, lon2), max(lat1, lat2)


# ---------------------------------------------------------------------------
# HTTP helpers. Python's default SSL context fails on this project's dev machine
# ("ASN1: NOT_ENOUGH_DATA" while loading the Windows certificate store), which
# used to send EVERY request -- each STAC page, each HEAD, each download --
# through a failed urllib attempt and then a freshly spawned `curl` process.
# A context built from certifi's CA bundle sidesteps the Windows store, and a
# keep-alive connection per thread turns a HEAD from ~200 ms into ~16 ms.
# `curl` stays as the fallback when no working context can be built at all.
# ---------------------------------------------------------------------------


def _make_ssl_context():
    try:
        return ssl.create_default_context()
    except ssl.SSLError:
        pass
    try:
        import certifi
        return ssl.create_default_context(cafile=certifi.where())
    except Exception:
        return None


SSL_CONTEXT = _make_ssl_context()
_local = threading.local()


class HttpError(Exception):
    def __init__(self, status, url):
        super().__init__(f"HTTP {status} for {url}")
        self.status = status


def _pool():
    pool = getattr(_local, "pool", None)
    if pool is None:
        pool = _local.pool = {}
    return pool


def _conn(scheme, host):
    pool = _pool()
    c = pool.get((scheme, host))
    if c is None:
        if scheme == "https":
            c = http.client.HTTPSConnection(host, context=SSL_CONTEXT, timeout=60)
        else:
            c = http.client.HTTPConnection(host, timeout=60)
        pool[(scheme, host)] = c
    return c


def _drop_conn(scheme, host):
    c = _pool().pop((scheme, host), None)
    if c is not None:
        c.close()


def _open(method, url, headers=None, redirects=5):
    """Sends a request on this thread's keep-alive connection and returns the
    response (the caller must read it to the end). Follows redirects; retries
    once on a connection the server has quietly closed."""
    for _ in range(redirects + 1):
        u = urllib.parse.urlsplit(url)
        path = (u.path or "/") + ("?" + u.query if u.query else "")
        h = {"User-Agent": USER_AGENT, "Accept-Encoding": "identity"}
        h.update(headers or {})
        resp = None
        for attempt in range(2):
            c = _conn(u.scheme, u.netloc)
            try:
                c.request(method, path, headers=h)
                resp = c.getresponse()
                break
            except (http.client.HTTPException, OSError):
                _drop_conn(u.scheme, u.netloc)
                if attempt:
                    raise
        if resp.status in (301, 302, 303, 307, 308):
            resp.read()
            url = urllib.parse.urljoin(url, resp.getheader("Location"))
            continue
        if resp.will_close:
            _pool().pop((u.scheme, u.netloc), None)
        return resp
    raise RuntimeError(f"too many redirects: {url}")


def _curl(args, capture=True):
    result = subprocess.run(["curl", "-sSL", "-A", USER_AGENT] + args, capture_output=capture)
    if result.returncode != 0:
        raise RuntimeError(f"curl failed ({result.returncode}): {' '.join(args)}")
    return result


def http_get_json(url, attempts=3):
    # A nationwide run makes well over a thousand of these (one per STAC page,
    # plus one HEAD per asset); at that volume a transient timeout or reset is
    # not a matter of if but when, and used to take the whole run down with it.
    last_error = None
    for attempt in range(attempts):
        try:
            if SSL_CONTEXT is not None:
                resp = _open("GET", url)
                body = resp.read()
                if resp.status != 200:
                    raise HttpError(resp.status, url)
                return json.loads(body)
            return json.loads(_curl([url]).stdout)
        except (HttpError, RuntimeError, http.client.HTTPException, OSError, json.JSONDecodeError) as e:
            last_error = e
        if attempt < attempts - 1:
            time.sleep(2 ** attempt)
    raise RuntimeError(f"giving up on {url} after {attempts} attempts: {last_error}")


def http_head(url, attempts=3):
    """Returns {size, etag, last_modified, sha256, ranges} for url, or None if
    it 404s or is still unreachable after retrying (a transient blip shouldn't
    permanently mark a file "unreachable" out of tens of thousands checked)."""
    headers = None
    if SSL_CONTEXT is not None:
        for attempt in range(attempts):
            try:
                resp = _open("HEAD", url)
                resp.read()
                if resp.status == 404:
                    return None
                if resp.status == 200:
                    headers = {k.lower(): v for k, v in resp.getheaders()}
                    break
            except (http.client.HTTPException, OSError):
                pass
            if attempt < attempts - 1:
                time.sleep(1 + attempt)
    else:
        result = subprocess.run(["curl", "-sSI", "-L", "-A", USER_AGENT, url],
                                 capture_output=True, text=True)
        if result.returncode == 0:
            headers = {}
            for line in result.stdout.splitlines():
                if ":" in line:
                    k, _, v = line.partition(":")
                    headers[k.strip().lower()] = v.strip()
    if not headers or "content-length" not in headers:
        return None

    size = headers.get("content-length")
    sha = headers.get("x-amz-meta-sha256")
    return {
        "size": int(size) if size else None,
        "etag": headers.get("etag"),
        "last_modified": headers.get("last-modified"),
        "sha256": sha.lower() if sha else None,
        "ranges": headers.get("accept-ranges", "").lower() == "bytes",
    }


class DiskFull(Exception):
    pass


class DiskBudget:
    """Keeps downloads from taking the drive below `reserve` bytes free. Each file
    takes a ticket for the bytes it still has to write and pays it down chunk by
    chunk as the writes land, so parallel downloads never promise the same space
    twice; closing a ticket (success or failure) returns whatever is left."""

    def __init__(self, path, reserve):
        self.path, self.reserve = path, reserve
        self.pending = 0
        self.lock = threading.Lock()

    def free(self):
        return shutil.disk_usage(self.path).free

    def try_take(self, nbytes):
        with self.lock:
            if self.free() - self.pending - nbytes < self.reserve:
                return None
            self.pending += nbytes
            return Ticket(self, nbytes)


class Ticket:
    def __init__(self, budget, nbytes):
        self.budget, self.left = budget, nbytes

    def written(self, nbytes):
        with self.budget.lock:
            n = min(nbytes, self.left)
            self.left -= n
            self.budget.pending -= n

    def close(self):
        self.written(self.left)


def _write_stream(resp, f, hasher, ticket, progress):
    while True:
        block = resp.read(CHUNK)
        if not block:
            return
        try:
            f.write(block)
        except OSError as e:
            if e.errno == errno.ENOSPC:
                raise DiskFull() from e
            raise
        if hasher is not None:
            hasher.update(block)
        ticket.written(len(block))
        progress(len(block))


def _download_single(url, tmp, size, sha256, ticket, progress):
    """One stream, resuming from an existing .part when the server allows it."""
    have = os.path.getsize(tmp) if os.path.exists(tmp) else 0
    if size is not None and have >= size:
        have = 0
    hasher = hashlib.sha256() if sha256 else None
    resp = _open("GET", url, {"Range": f"bytes={have}-"} if have else None)
    if resp.status == 206 and have:
        if hasher is not None:
            with open(tmp, "rb") as f:
                for block in iter(lambda: f.read(CHUNK), b""):
                    hasher.update(block)
        # already on disk, so never part of the reservation
        progress(have)
        mode = "ab"
    elif resp.status == 200:
        mode = "wb"
    else:
        resp.read()
        raise HttpError(resp.status, url)
    with open(tmp, mode) as f:
        _write_stream(resp, f, hasher, ticket, progress)
    return hasher.hexdigest() if hasher is not None else None


def _download_segmented(url, tmp, size, sha256, ticket, progress):
    """Parallel byte ranges into one preallocated file; each range runs on its
    own thread, so on its own keep-alive connection."""
    with open(tmp, "wb") as f:
        f.truncate(size)
    count = int(min(MAX_SEGMENTS, max(2, size // SEGMENT_BYTES)))
    step = -(-size // count)

    def fetch(start):
        end = min(size, start + step) - 1
        resp = _open("GET", url, {"Range": f"bytes={start}-{end}"})
        if resp.status != 206:
            resp.read()
            raise HttpError(resp.status, url)
        with open(tmp, "r+b") as f:
            f.seek(start)
            _write_stream(resp, f, None, ticket, progress)

    with concurrent.futures.ThreadPoolExecutor(max_workers=count) as pool:
        for fut in [pool.submit(fetch, start) for start in range(0, size, step)]:
            fut.result()
    if not sha256:
        return None
    hasher = hashlib.sha256()
    with open(tmp, "rb") as f:
        for block in iter(lambda: f.read(CHUNK), b""):
            hasher.update(block)
    return hasher.hexdigest()


def download_file(url, dest, head, sha256, ticket, progress, attempts=3):
    """Downloads url to dest (through dest.part), verifying the SHA-256 when one
    is known. `ticket` holds the space reserved for it."""
    tmp = dest + ".part"
    size = head.get("size")
    expected = (sha256 or head.get("sha256") or "").lower() or None

    if SSL_CONTEXT is None:
        _curl(["-C", "-", "-o", tmp, url], capture=False)
        ticket.written(size or 0)
        progress(size or 0)
        os.replace(tmp, dest)
        return

    last_error = None
    for attempt in range(attempts):
        done_before = [0]

        def counted(n):
            done_before[0] += n
            progress(n)
        try:
            if size and size >= SEGMENT_MIN_BYTES and head.get("ranges"):
                got = _download_segmented(url, tmp, size, expected, ticket, counted)
            else:
                got = _download_single(url, tmp, size, expected, ticket, counted)
            if size is not None and os.path.getsize(tmp) != size:
                raise RuntimeError(f"size mismatch: got {os.path.getsize(tmp)}, expected {size}")
            if expected and got != expected:
                os.remove(tmp)  # corrupt; resuming would only extend the damage
                raise RuntimeError("SHA-256 mismatch")
            os.replace(tmp, dest)
            return
        except DiskFull:
            if os.path.exists(tmp):
                os.remove(tmp)
            raise
        except (HttpError, RuntimeError, http.client.HTTPException, OSError) as e:
            last_error = e
            # a retry re-counts whatever it resumes from; keep the progress figure honest
            progress(-done_before[0])
            if attempt < attempts - 1:
                time.sleep(2 ** attempt)
    raise RuntimeError(f"{os.path.basename(dest)}: {last_error}")


def sha256_of_asset(asset):
    """STAC's `checksum:multihash` is 0x12 (sha2-256), 0x20 (32 bytes), digest."""
    mh = (asset.get("checksum:multihash") or "").lower()
    return mh[4:] if mh.startswith("1220") and len(mh) == 68 else None


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


def known_current(dest, entry, sha256):
    """True when the manifest already recorded this exact file (same checksum as
    STAC publishes now, same size on disk) -- decided without any request."""
    return (sha256 is not None and entry is not None and entry.get("sha256") == sha256
            and os.path.exists(dest) and os.path.getsize(dest) == entry.get("size"))


def is_up_to_date(dest, entry, head):
    if not os.path.exists(dest):
        return False
    local_size = os.path.getsize(dest)

    # No manifest entry -- typically a file downloaded by hand before this tool
    # existed (the region's original ~155 GB was fetched that way). Rather than
    # re-fetching everything on the first run, treat a matching size as "no
    # change": the caller backfills a manifest entry from this HEAD afterwards,
    # so later runs get the precise ETag/Last-Modified check below for free.
    if entry is None:
        return head.get("size") is not None and head["size"] == local_size

    if local_size != entry.get("size"):
        return False
    if head.get("sha256") and entry.get("sha256") and head["sha256"] != entry["sha256"]:
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


def _stac_pages(collection, bbox_wgs84):
    params = {"limit": "100"}  # the API's ceiling
    if bbox_wgs84:
        params["bbox"] = ",".join(f"{v:.6f}" for v in bbox_wgs84)
    url = f"{STAC_BASE}/collections/{collection}/items?" + urllib.parse.urlencode(params)
    while url:
        page = http_get_json(url)
        yield page.get("features", [])
        url = next((link["href"] for link in page.get("links", []) if link.get("rel") == "next"), None)


def stac_items(collection, bbox_wgs84=None):
    """Every item of the collection (inside bbox). The API pages 100 items at a
    time behind a cursor, so a single query is inherently sequential -- a
    nationwide listing is hundreds of round trips back to back. A large bbox is
    therefore split into sub-boxes listed concurrently; an item straddling two
    of them comes back twice and is deduped by id."""
    if bbox_wgs84 is None:
        parts = [None]
    else:
        # ~0.1 deg cells (about 8 x 11 km): a page or two each, plenty of parallelism
        x0, y0, x1, y1 = bbox_wgs84
        nx = max(1, min(40, round((x1 - x0) / 0.1)))
        ny = max(1, min(40, round((y1 - y0) / 0.1)))
        parts = [(x0 + (x1 - x0) * i / nx, y0 + (y1 - y0) * j / ny,
                  x0 + (x1 - x0) * (i + 1) / nx, y0 + (y1 - y0) * (j + 1) / ny)
                 for i in range(nx) for j in range(ny)]

    seen = set()
    items = []
    lock = threading.Lock()

    def list_part(part):
        for features in _stac_pages(collection, part):
            with lock:
                before = len(items)
                for f in features:
                    if f.get("id") not in seen:
                        seen.add(f.get("id"))
                        items.append(f)
                # A nationwide query is hundreds of pages; without this a large
                # bbox prints nothing at all until every page has been fetched.
                step = PROGRESS_EVERY * 5
                if len(items) // step != before // step:
                    print(f"  ...{len(items)} {collection} item(s) listed so far", flush=True)

    with concurrent.futures.ThreadPoolExecutor(max_workers=STAC_WORKERS) as pool:
        for fut in [pool.submit(list_part, p) for p in parts]:
            fut.result()
    return items


def bbox_span(feature):
    b = feature.get("bbox") or [0, 0, 0, 0]
    return max(b[2] - b[0], b[3] - b[1])


# ---------------------------------------------------------------------------
# Dataset resolvers: each yields (url, filename) pairs to fetch into a subdir.
# ---------------------------------------------------------------------------


def load_tiles_file(path):
    """A --tiles-file: one 1 km tile per line as "E-N" in kilometres (e.g. 2583-1113), the
    same key swisstopo uses in its item ids. Blank lines and #comments are ignored."""
    tiles = set()
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.split("#", 1)[0].strip()
            if line:
                e, n = line.replace(",", "-").replace("_", "-").split("-")
                tiles.add((int(e), int(n)))
    if not tiles:
        sys.exit(f"error: {path} lists no tiles")
    return tiles


def tiles_bbox_lv95(tiles):
    return (min(e for e, _ in tiles) * 1000.0, min(n for _, n in tiles) * 1000.0,
            (max(e for e, _ in tiles) + 1) * 1000.0, (max(n for _, n in tiles) + 1) * 1000.0)


def apply_tiles_file(args):
    """--tiles-file narrows a dataset to exactly those tiles. The STAC query itself only takes a
    rectangle, so it is sent the tiles' bounding box and the result is filtered: a painted,
    irregular selection then fetches its own tiles and none of the rectangle's other corners."""
    args.tiles = load_tiles_file(args.tiles_file) if getattr(args, "tiles_file", None) else None
    if args.tiles and not args.bbox:
        args.bbox = list(tiles_bbox_lv95(args.tiles))


def item_lv95_bounds(feature):
    """An item's footprint as an LV95 rectangle, from its polygon. Its WGS84 bbox would do too,
    but a lon/lat box drawn around a sheet cut on the LV95 grid is tens of metres too big on every
    side -- enough to count the neighbouring sheets as touching."""
    ring = ((feature.get("geometry") or {}).get("coordinates") or [[]])[0]
    if len(ring) >= 3:
        pts = [wgs84_to_lv95(lat, lon) for lon, lat in ring]
    else:
        b = feature.get("bbox") or [0, 0, 0, 0]
        pts = [wgs84_to_lv95(b[1], b[0]), wgs84_to_lv95(b[3], b[2])]
    return (min(p[0] for p in pts), min(p[1] for p in pts), max(p[0] for p in pts), max(p[1] for p in pts))


def item_touches_tiles(feature, tiles):
    """Whether a STAC item's footprint overlaps any of the 1 km tiles. Strictly, and a few metres
    inside each tile: sheets are cut on the same kilometre lines, and one that merely shares an
    edge with a tile holds none of its buildings."""
    x0, y0, x1, y1 = item_lv95_bounds(feature)
    for e, n in tiles:
        if e * 1000.0 + 5 < x1 and (e + 1) * 1000.0 - 5 > x0 and n * 1000.0 + 5 < y1 and (n + 1) * 1000.0 - 5 > y0:
            return True
    return False


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
        if args.tiles is not None and tuple(int(x) for x in tile.split("-")) not in args.tiles:
            continue
        if tile not in latest_by_tile or year > latest_by_tile[tile][0]:
            latest_by_tile[tile] = (year, feature)

    for _year, feature in latest_by_tile.values():
        for key, asset in feature.get("assets", {}).items():
            if pattern.search(key):
                yield asset["href"], key, sha256_of_asset(asset)


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
        yield asset["href"], key, sha256_of_asset(asset)


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
    if args.tiles is not None and not args.nationwide:
        wanted = [f for f in wanted if item_touches_tiles(f, args.tiles)]

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
                yield asset["href"], key, sha256_of_asset(asset)


def resolve_gwr(args):
    url = f"https://public.madd.bfs.admin.ch/{args.canton}.zip"
    yield url, f"gwr_{args.canton}.zip", None


def _resolve_stac_gdb(collection):
    """ASTRA's route networks: one STAC item, asset key already matches the
    local filename convention (e.g. veloland_2056.gdb.zip)."""
    def resolver(args):
        pattern = re.compile(r"_2056\.gdb\.zip$")
        for feature in stac_items(collection):
            for key, asset in feature.get("assets", {}).items():
                if pattern.search(key):
                    yield asset["href"], key, sha256_of_asset(asset)
    return resolver


GEOFABRIK_EUROPE = "https://download.geofabrik.de/europe/"


def resolve_osm(args):
    """Geofabrik's Switzerland extract. `switzerland-latest.osm.pbf` has been seen answering with a
    301 to itself (a redirect loop), so take the newest dated `switzerland-YYMMDD.osm.pbf` the
    index lists, and only fall back to -latest when the index shows none."""
    try:
        if SSL_CONTEXT is not None:
            resp = _open("GET", GEOFABRIK_EUROPE)
            html = resp.read().decode("utf-8", "replace")
        else:
            html = _curl([GEOFABRIK_EUROPE]).stdout.decode("utf-8", "replace")
    except (RuntimeError, http.client.HTTPException, OSError) as e:
        print(f"warning: could not list {GEOFABRIK_EUROPE}: {e}", file=sys.stderr)
        html = ""
    dates = sorted(set(re.findall(r"switzerland-(\d{6})\.osm\.pbf", html)))
    name = f"switzerland-{dates[-1]}.osm.pbf" if dates else "switzerland-latest.osm.pbf"
    yield GEOFABRIK_EUROPE + name, name, None


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
    "osm": {
        "subdir": "osm",
        "resolve": resolve_osm,
        "help": "OpenStreetMap Switzerland extract (Geofabrik, ODbL; optional road attribute overlay)",
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


TILE_RE = re.compile(r"_(\d{4})-(\d{4})[_.]")


def centre_distance(filename, centre):
    """Squared distance from the bbox centre to the km tile named in filename
    (0 for a file that names no tile, e.g. a nationwide asset)."""
    m = TILE_RE.search(filename)
    if not m or centre is None:
        return 0
    e = int(m.group(1)) * 1000 + 500
    n = int(m.group(2)) * 1000 + 500
    return (e - centre[0]) ** 2 + (n - centre[1]) ** 2


def emit(args, event, **fields):
    """--progress-json: one machine-readable line per event, prefixed so a caller can pick it
    out of the ordinary human output interleaved with it (tools/MapSetup does)."""
    if getattr(args, "progress_json", False):
        print("@progress " + json.dumps({"event": event, **fields}), flush=True)


def fmt_bytes(n):
    return f"{n / 1e9:.2f} GB" if abs(n) >= 1e9 else f"{n / 1e6:.1f} MB"


def run(args):
    spec = DATASETS[args.dataset]
    out_dir = args.out or os.path.join(DATA_ROOT, spec["subdir"])
    os.makedirs(out_dir, exist_ok=True)
    manifest = load_manifest(out_dir)
    if SSL_CONTEXT is None:
        print("warning: no usable SSL context (install certifi); falling back to one curl "
              "process per request, which is much slower", file=sys.stderr)

    apply_tiles_file(args)
    clock = time.time()
    print(f"resolving {args.dataset} assets...", flush=True)
    emit(args, "stage", name="resolve")
    candidates = list(spec["resolve"](args))
    print(f"{len(candidates)} candidate file(s) listed in {time.time() - clock:.1f}s", flush=True)

    # Decided locally: the manifest already holds this exact file, by the
    # checksum STAC publishes -- no request needed at all.
    to_check = []
    unchanged = 0
    for url, filename, sha in candidates:
        if not args.force and known_current(os.path.join(out_dir, filename), manifest.get(filename), sha):
            unchanged += 1
        else:
            to_check.append((url, filename, sha))
    if to_check:
        print(f"{unchanged} already current by checksum; checking {len(to_check)} against the "
              f"server ({HEAD_CHECK_WORKERS} at a time)...", flush=True)

    def check(item):
        url, filename, sha = item
        return url, filename, sha, http_head(url)

    plan = []  # (url, filename, dest, head, sha)
    unreachable = 0
    checked = 0
    # Verbose per-file output is useful for a handful of files and unreadable
    # noise for tens of thousands, so only chatter file-by-file below that.
    chatty = len(candidates) <= 50

    with concurrent.futures.ThreadPoolExecutor(max_workers=HEAD_CHECK_WORKERS) as pool:
        for url, filename, sha, head in pool.map(check, to_check):
            checked += 1
            if checked % 50 == 0 or checked == len(to_check):
                emit(args, "check", done=checked, total=len(to_check))
            if not chatty and (checked % PROGRESS_EVERY == 0 or checked == len(to_check)):
                print(f"  ...checked {checked}/{len(to_check)} "
                      f"({len(plan)} to fetch, {unchanged} unchanged, {unreachable} unreachable)",
                      flush=True)

            dest = os.path.join(out_dir, filename)
            if head is None:
                unreachable += 1
                if chatty:
                    print(f"  ! could not reach {url}, skipping")
                continue
            entry = manifest.get(filename)
            if not args.force and is_up_to_date(dest, entry, head):
                unchanged += 1
                if entry is None:
                    # First time this tool has seen a file that was already on
                    # disk (e.g. from the original manual download) -- record
                    # it now so later runs can decide from the checksum alone.
                    manifest[filename] = entry = {
                        "url": url,
                        "size": head["size"],
                        "etag": head.get("etag"),
                        "last_modified": head.get("last_modified"),
                        "downloaded_at": None,  # predates this tool; not actually downloaded now
                    }
                if not entry.get("sha256"):
                    entry["sha256"] = sha or head.get("sha256")
                if chatty:
                    print(f"  = {filename} (unchanged, {head['size'] / 1e6:.1f} MB)")
                continue
            plan.append((url, filename, dest, head, sha))

    save_manifest(out_dir, manifest)

    if unreachable:
        print(f"{unreachable} file(s) could not be reached and were skipped", flush=True)

    if not plan:
        print("nothing to download.")
        emit(args, "done", files=0, bytes=0, seconds=0, errors=0, nospace=0)
        return

    # nearest the middle of the requested area first, so a download that stops
    # early (full disk, Ctrl+C) leaves one contiguous block rather than a scatter
    if args.bbox:
        centre = ((args.bbox[0] + args.bbox[2]) / 2, (args.bbox[1] + args.bbox[3]) / 2)
        plan.sort(key=lambda p: centre_distance(p[1], centre))

    total = sum(h["size"] or 0 for _, _, _, h, _ in plan)
    print(f"{len(plan)} file(s) to fetch, {fmt_bytes(total)} total", flush=True)
    emit(args, "plan", files=len(plan), bytes=total, unchanged=unchanged)

    if args.dry_run:
        shown = plan if chatty else plan[:20]
        for url, filename, _, head, _ in shown:
            size = f"{head['size'] / 1e6:.1f} MB" if head["size"] else "unknown size"
            print(f"  {filename}  ({size})  {url}")
        if len(shown) < len(plan):
            print(f"  ...and {len(plan) - len(shown)} more")
        return

    reserve = int(args.reserve_mb * 1024 * 1024)
    budget = DiskBudget(out_dir, reserve)
    free = budget.free()
    if not args.fill_disk and total * DISK_HEADROOM + reserve > free:
        print(f"error: need ~{fmt_bytes(total * DISK_HEADROOM + reserve)} free, "
              f"only {fmt_bytes(free)} available on the drive holding {out_dir}.\n"
              f"       Pass --fill-disk to download what fits, keeping {args.reserve_mb:g} MB free.",
              file=sys.stderr)
        sys.exit(1)
    if args.fill_disk:
        room = max(0, free - reserve)
        print(f"--fill-disk: {fmt_bytes(free)} free, keeping {args.reserve_mb:g} MB -> room for "
              f"{'everything' if room >= total else '~' + fmt_bytes(room) + ' of it'}", flush=True)

    # ---- parallel downloads ------------------------------------------------------------
    lock = threading.Lock()
    moved = [0]
    stop = threading.Event()

    def progress(n):
        with lock:
            moved[0] += n

    def fetch(item):
        url, filename, dest, head, sha = item
        if stop.is_set():
            return "skipped", item, None
        size = head.get("size") or 0
        part = dest + ".part"
        have = os.path.getsize(part) if os.path.exists(part) else 0
        ticket = budget.try_take(max(0, size - (have if have < size else 0)))
        if ticket is None:
            return "nospace", item, None
        try:
            download_file(url, dest, head, sha, ticket, progress)
            return "ok", item, None
        except DiskFull:
            stop.set()  # something else is writing to this drive too
            return "nospace", item, "disk full"
        except Exception as e:  # reported and counted, never fatal for the rest
            return "error", item, str(e)
        finally:
            ticket.close()

    done = fetched = nospace = 0
    errors = []
    started = time.time()
    last_report = last_save = last_json = started
    print(f"downloading with {args.jobs} parallel job(s)...", flush=True)

    pool = concurrent.futures.ThreadPoolExecutor(max_workers=args.jobs)
    try:
        futures = [pool.submit(fetch, item) for item in plan]
        for fut in concurrent.futures.as_completed(futures):
            status, (url, filename, dest, head, sha), error = fut.result()
            done += 1
            if status == "ok":
                fetched += 1
                manifest[filename] = {
                    "url": url,
                    "size": os.path.getsize(dest),
                    "etag": head.get("etag"),
                    "last_modified": head.get("last_modified"),
                    "sha256": sha or head.get("sha256"),
                    "downloaded_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
                }
                if chatty:
                    print(f"  > [{done}/{len(plan)}] {filename} ({(head['size'] or 0) / 1e6:.1f} MB)", flush=True)
            elif status in ("nospace", "skipped"):
                nospace += 1
            elif status == "error":
                errors.append(f"{filename}: {error}")
                print(f"  ! {error}", flush=True)

            now = time.time()
            if now - last_json >= 1 or done == len(plan):
                emit(args, "progress", done=done, total=len(plan), bytes=moved[0], bytes_total=total,
                     rate=moved[0] / max(0.001, now - started))
                last_json = now
            if now - last_save > 10:
                save_manifest(out_dir, manifest)
                last_save = now
            if not chatty and (now - last_report > 3 or done == len(plan)):
                rate = moved[0] / max(0.001, now - started)
                left = max(0, total - moved[0])
                print(f"  [{done}/{len(plan)}] {fmt_bytes(moved[0])} of {fmt_bytes(total)}, "
                      f"{rate / 1e6:.0f} MB/s, eta {left / max(rate, 1):.0f}s, "
                      f"{fmt_bytes(budget.free())} free"
                      + (f", {nospace} did not fit" if nospace else ""), flush=True)
                last_report = now
    except KeyboardInterrupt:
        print("\ninterrupted -- finished files are recorded, partial ones resume next run", flush=True)
        pool.shutdown(wait=False, cancel_futures=True)
        save_manifest(out_dir, manifest)
        sys.exit(130)
    pool.shutdown()
    save_manifest(out_dir, manifest)

    elapsed = time.time() - started
    emit(args, "done", files=fetched, bytes=moved[0], seconds=elapsed, errors=len(errors), nospace=nospace)
    print(f"done: {fetched} file(s), {fmt_bytes(moved[0])} in {elapsed:.0f}s "
          f"({moved[0] / max(elapsed, 0.001) / 1e6:.0f} MB/s) written to {out_dir}")
    if nospace:
        print(f"{nospace} file(s) did not fit (keeping {args.reserve_mb:g} MB free); "
              f"re-run on a drive with more room, or after freeing space, to get the rest")
    if errors:
        print(f"{len(errors)} file(s) failed; re-run to retry them", file=sys.stderr)
        sys.exit(1)


def _add_run_options(ap, defaults):
    # Registered on the top-level parser AND on every dataset subparser, so they work on either
    # side of the dataset name ("--fill-disk swissalti3d ..." or "swissalti3d ... --fill-disk").
    # The subparser copies default to SUPPRESS, or they would reset what was given before it.
    d = (lambda v: v) if defaults else (lambda v: argparse.SUPPRESS)
    ap.add_argument("--dry-run", action="store_true", default=d(False),
                    help="print what would be downloaded, then exit")
    ap.add_argument("--force", action="store_true", default=d(False),
                    help="re-download even if the manifest says up to date")
    ap.add_argument("--out", metavar="DIR", default=d(None),
                    help="download into DIR instead of ressources/data/<dataset dir> (e.g. another drive)")
    ap.add_argument("--jobs", type=int, default=d(DOWNLOAD_WORKERS),
                    help=f"files downloaded in parallel (default {DOWNLOAD_WORKERS})")
    ap.add_argument("--fill-disk", action="store_true", default=d(False),
                    help="download what fits instead of refusing when everything does not; "
                         "stops at --reserve-mb free, nearest-to-bbox-centre tiles first")
    ap.add_argument("--reserve-mb", type=float, default=d(500),
                    help="free space never to go below, in MB (default 500)")
    ap.add_argument("--progress-json", action="store_true", default=d(False),
                    help="also print machine-readable '@progress {json}' lines (used by tools/MapSetup)")


def build_parser():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--list", action="store_true", help="list available datasets and exit")
    _add_run_options(ap, defaults=True)

    sub = ap.add_subparsers(dest="dataset")
    for name, spec in DATASETS.items():
        p = sub.add_parser(name, help=spec["help"])
        _add_run_options(p, defaults=False)
        if spec["needs_bbox"]:
            p.add_argument("--bbox", nargs=4, type=float, default=None,
                            metavar=("MINE", "MINN", "MAXE", "MAXN"),
                            help="LV95 (EPSG:2056) bounding box (this or --tiles-file is required)")
        else:
            p.add_argument("--bbox", nargs=4, type=float, default=None,
                            metavar=("MINE", "MINN", "MAXE", "MAXN"),
                            help="LV95 (EPSG:2056) bounding box (optional, dataset is nationwide)")
        if name in ("swissalti3d", "swissbuildings3d"):
            p.add_argument("--tiles-file", metavar="FILE", default=None,
                            help="only these 1 km tiles, one 'E-N' (km) per line, e.g. 2583-1113")
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

    if DATASETS[args.dataset]["needs_bbox"] and not args.bbox and not getattr(args, "tiles_file", None):
        ap.error(f"{args.dataset} needs --bbox or --tiles-file")
    run(args)


if __name__ == "__main__":
    main()

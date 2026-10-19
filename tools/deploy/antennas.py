#!/usr/bin/env python3
"""The live map's antennas (#896): OFCOM's mobile base stations, outdoor 4G sites only, compact.

    python tools/deploy/antennas.py [out.json]     (default test_output/web-antennas/antennas-4g.json)

Downloads "Locations of mobile phone base stations" (ch.bakom.standorte-mobilfunkanlagen, OFCOM,
data.geo.admin.ch, opendata.swiss terms: attribution) and writes, per operator, each outdoor site
that has 4G as [lat, lon, power class] (0 up to 6 W, 1 up to 500 W, 2 up to 5,000 W, 3 over
5,000 W ERP). Indoor and tunnel sites are left out: they cover the inside of a building or a tube.
The page (tools/deploy/web/map.html) turns the power class into a simulated reach.
"""
import json
import os
import subprocess
import sys
import time
import urllib.request

URL = ("https://data.geo.admin.ch/ch.bakom.standorte-mobilfunkanlagen/standorte-mobilfunkanlagen/"
       "standorte-mobilfunkanlagen_2056.json")
OPERATORS = ("Swisscom", "Sunrise", "Salt")

# The brands people buy (October 2026) and whose antennas they use: only the three operators own any.
# "own" = the network's own brand. Sources disagree on a few (Lycamobile, Ortel Mobile): the majority's.
# Checked against handyabo.com, dschungelkompass.ch and internet-offer.ch; see docs/notes/net/live-map.md.
PROVIDERS = {
    "Swisscom": {"own": ["Swisscom", "Wingo"], "rent": ["M-Budget Mobile", "Coop Mobile", "Mucho Mobile"]},
    "Sunrise": {"own": ["Sunrise", "yallo", "UPC"],
                "rent": ["Lebara", "Aldi Suisse Mobile", "Digital Republic", "Galaxus Mobile", "Quickline", "Teleboy",
                         "TalkTalk", "swype", "ok.- mobile", "Ortel Mobile", "CHmobile", "SAK", "Thurcom", "TalkEasy"]},
    "Salt": {"own": ["Salt", "GoMo", "Das Abo"],
             "rent": ["Lidl Connect", "Post Mobile", "spusu", "Lycamobile", "iWay", "VTX"]},
}


def wgs84(e, n):
    """LV95 to WGS84, swisstopo's approximate formulas (about a metre off: plenty for a map)."""
    y = (e - 2600000) / 1e6
    x = (n - 1200000) / 1e6
    lon = 2.6779094 + 4.728982 * y + 0.791484 * y * x + 0.1306 * y * x * x - 0.0436 * y ** 3
    lat = 16.9023892 + 3.238272 * x - 0.270978 * y * y - 0.002528 * x * x - 0.0447 * y * y * x - 0.0140 * x ** 3
    return lat * 100 / 36, lon * 100 / 36


def fetch():
    """The GeoJSON (27 MB). curl when Python's TLS cannot (a broken Windows certificate store)."""
    try:
        with urllib.request.urlopen(URL, timeout=120) as r:
            return r.read().decode("utf-8")
    except Exception as e:  # noqa: BLE001 - any failure: try curl before giving up
        print(f"[antennas] urllib failed ({e}), trying curl")
        return subprocess.run(["curl", "-sSfL", "--max-time", "300", URL], check=True, capture_output=True).stdout.decode("utf-8")


def power_class(text):
    for i, word in enumerate((": very low", ": low", ": medium", ": high")):
        if word in text:
            return i
    return 1


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.join("test_output", "web-antennas", "antennas-4g.json")
    features = json.loads(fetch())["features"]
    sites = {op: [] for op in OPERATORS}
    for f in features:
        p = f["properties"]
        op = p["station"].split(" ")[0]
        if op not in sites or "4G" not in p.get("techno_en", "") or not p.get("typ_en", "").startswith("Outdoor"):
            continue
        e, n = f["geometry"]["coordinates"][:2]
        lat, lon = wgs84(e, n)
        sites[op].append([round(lat, 5), round(lon, 5), power_class(p.get("power_en", ""))])
    data = {
        "source": "Federal Office of Communications OFCOM, locations of mobile phone base stations (opendata.swiss)",
        "fetched": time.strftime("%Y-%m-%d"),
        "sites": sites,
        "providers": PROVIDERS,
    }
    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    with open(out + ".tmp", "w", encoding="utf-8") as fh:
        json.dump(data, fh, separators=(",", ":"))
    os.replace(out + ".tmp", out)
    print(f"[antennas] {out}: " + ", ".join(f"{op} {len(v)}" for op, v in sites.items()))


if __name__ == "__main__":
    main()

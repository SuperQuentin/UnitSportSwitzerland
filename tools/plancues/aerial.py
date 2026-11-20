"""The swissimage aerial over an LV95 box, fetched from the WMS in docs/notes/terrain/stadiums.md, cached on disk."""
import hashlib
import os
import subprocess
import urllib.request

import cv2
import numpy as np

WMS = ("https://wms.geo.admin.ch/?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap&LAYERS=ch.swisstopo.swissimage&STYLES="
       "&CRS=EPSG:2056&BBOX={e0},{n0},{e1},{n1}&WIDTH={w}&HEIGHT={h}&FORMAT=image/jpeg")
PX_PER_M = 3


def fetch(e0, n0, e1, n1, cache_dir, px_per_m=PX_PER_M):
    """RGB array of the aerial over the box (rows north to south); the box is snapped to whole metres."""
    e0, n0, e1, n1 = int(e0), int(n0), int(e1) + 1, int(n1) + 1
    w, h = (e1 - e0) * px_per_m, (n1 - n0) * px_per_m
    key = hashlib.sha1(("%d,%d,%d,%d,%d" % (e0, n0, e1, n1, px_per_m)).encode()).hexdigest()[:12]
    path = os.path.join(cache_dir, "aerial_%s.jpg" % key)
    if not os.path.exists(path):
        # the box as the stadiums note writes it: E0,N0,E1,N1
        url = WMS.format(e0=e0, n0=n0, e1=e1, n1=n1, w=w, h=h)
        os.makedirs(cache_dir, exist_ok=True)
        try:
            req = urllib.request.Request(url, headers={"User-Agent": "plancues/1"})
            with urllib.request.urlopen(req, timeout=60) as r:
                data = r.read()
            with open(path, "wb") as f:
                f.write(data)
        except Exception:  # some Windows Python builds cannot load the certificate store: curl can
            subprocess.run(["curl", "-s", "-f", "-o", path, url], check=True, timeout=120)
    img = cv2.imread(path)
    if img is None:
        raise RuntimeError("aerial not readable: " + path)
    return img[:, :, ::-1], (e0, n0, e1, n1), px_per_m


def lv95_to_px(e, n, box, px_per_m):
    e0, n0, e1, n1 = box
    return (np.asarray(e) - e0) * px_per_m, (n1 - np.asarray(n)) * px_per_m

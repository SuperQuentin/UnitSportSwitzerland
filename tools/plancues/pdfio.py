"""Opening documents, rendering pages, content hashes. Pages are 1-based everywhere user-facing."""
import hashlib
import os
import warnings

import numpy as np

warnings.filterwarnings("ignore")
import pymupdf  # noqa: E402

pymupdf.TOOLS.mupdf_display_errors(False)

IMAGE_EXT = {".jpg", ".jpeg", ".png", ".tif", ".tiff", ".svg"}
SKIP_EXT = {".db"}

DEFAULT_ROOT = os.environ.get("PLANCUES_ROOT", "X:/UnitSportSwitzerland")
DEFAULT_OUT = os.environ.get("PLANCUES_OUT", "X:/UnitSportSwitzerland/plancues/out")
DEFAULT_GWR = os.environ.get("PLANCUES_GWR", "X:/UnitSportSwitzerland/data/gwr/data.sqlite")
DEFAULT_CHUNKS = os.environ.get("PLANCUES_CHUNKS", "X:/UnitSportSwitzerland/terrain_chunks")


def rel(path, root=DEFAULT_ROOT):
    return os.path.relpath(path, root).replace("\\", "/")


def venue_dirs(root, venue):
    """Folders to scan for a venue name ('Lausanne_Stade_de_la_Tuiliere') or 'all'."""
    out = []
    for group in ("Sport", "Universities", "Landmarks"):
        g = os.path.join(root, group)
        if not os.path.isdir(g):
            continue
        for v in sorted(os.listdir(g)):
            if venue in ("all", v, group + "/" + v):
                out.append(os.path.join(g, v))
    return out


def list_documents(root, venue):
    files = []
    for d in venue_dirs(root, venue):
        for dp, _, fn in os.walk(d):
            for f in sorted(fn):
                ext = os.path.splitext(f)[1].lower()
                if ext == ".pdf" or ext in IMAGE_EXT:
                    files.append(os.path.join(dp, f))
    return sorted(files)


def venue_of(path, root=DEFAULT_ROOT):
    parts = rel(path, root).split("/")
    return parts[1] if len(parts) > 1 else parts[0]


def file_sig(path):
    st = os.stat(path)
    return "%d_%d" % (st.st_size, int(st.st_mtime))


def sha1_file(path):
    h = hashlib.sha1()
    with open(path, "rb") as f:
        for blk in iter(lambda: f.read(1 << 20), b""):
            h.update(blk)
    return h.hexdigest()


def open_doc(path):
    """Open a PDF or image as a pymupdf Document (images become one-page documents)."""
    return pymupdf.open(path)


def render(page, width_px=None, scale=None, clip=None, gray=False):
    """Render a page (or clip rectangle) to an RGB (or gray) numpy array."""
    r = clip or page.rect
    z = scale if scale else width_px / r.width
    pm = page.get_pixmap(matrix=pymupdf.Matrix(z, z), clip=clip, colorspace=pymupdf.csGRAY if gray else pymupdf.csRGB, alpha=False)
    a = np.frombuffer(pm.samples, dtype=np.uint8).reshape(pm.height, pm.width, pm.n)
    return a[:, :, 0] if gray else a


def save_png(path, arr):
    import cv2
    os.makedirs(os.path.dirname(path), exist_ok=True)
    cv2.imwrite(path, arr[:, :, ::-1] if arr.ndim == 3 else arr)

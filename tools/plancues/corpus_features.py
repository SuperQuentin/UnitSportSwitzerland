"""Builds (and caches on disk) the features of every page of a venue or the whole corpus."""
import json
import os
import sys
import time

import os as _os, sys as _sys
_sys.path.insert(0, _os.path.dirname(_os.path.abspath(__file__)))
import features
import pdfio


def cache_path(out):
    return os.path.join(out, "_cache", "features.json")


def load_cache(out):
    try:
        with open(cache_path(out)) as f:
            return json.load(f)
    except (OSError, ValueError):
        return {}


def save_cache(out, cache):
    p = cache_path(out)
    os.makedirs(os.path.dirname(p), exist_ok=True)
    try:
        with open(p + ".tmp", "w") as f:
            json.dump(cache, f)
        os.replace(p + ".tmp", p)
    except OSError:  # network shares sometimes refuse the rename: write in place
        with open(p, "w") as f:
            json.dump(cache, f)


def collect(root, out, venue, log=print):
    """Yield a feature dict (path, page 1-based, features or error) for every page of the venue's documents."""
    cache = load_cache(out)
    dirty = 0
    for path in pdfio.list_documents(root, venue):
        rp = pdfio.rel(path, root)
        key = rp + "|" + pdfio.file_sig(path) + "|v" + str(features.VERSION)
        if key in cache:
            for item in cache[key]:
                yield item
            continue
        items = []
        try:
            doc = pdfio.open_doc(path)
            for i in range(len(doc)):
                try:
                    f = features.page_features(doc, i)
                    items.append(dict(path=rp, page=i + 1, **f))
                except Exception as e:  # one bad page must not stop the run
                    items.append(dict(path=rp, page=i + 1, error="page failed: %s" % str(e)[:200]))
        except Exception as e:
            items.append(dict(path=rp, page=1, error="cannot open: %s" % str(e)[:200]))
        cache[key] = items
        dirty += 1
        if dirty % 10 == 0:
            save_cache(out, cache)
        log("features %s: %d page(s)" % (rp, len(items)))
        for item in items:
            yield item
    if dirty:
        save_cache(out, cache)


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    t = time.time()
    n = 0
    v = sys.argv[1] if len(sys.argv) > 1 else "all"
    for _ in collect(pdfio.DEFAULT_ROOT, pdfio.DEFAULT_OUT, v, log=lambda s: print(s, flush=True)):
        n += 1
    print("done", n, "pages in", round(time.time() - t), "s")

"""A small trained page-type classifier (scikit-learn) on the page features, replacing the hand rules of sortpages.classify.

Features: vector statistics (paths, lines, orthogonal share, wall weights, tiny repeated shapes), raster statistics (flat colours,
soft gradients, straight and orthogonal Hough lines, closed rooms, ink), text layer density, scale ratios and keyword votes of the
text and of the file name in FR/DE/IT/EN. Trained on the hand labels (sort_labels.csv, sort_labels_train.csv); the held-out
sort_labels_holdout.csv is never read here. A page whose best probability is under THRESH is `unsure`.
"""
import math
import os
import re

import numpy as np

import sortpages as sp

THRESH = 0.55  # below this best-class probability the page is unsure (the model vote, then a human, decides)
STATS = ["n_paths", "n_lines", "n_curves", "n_fill", "n_dashed", "n_words", "n_chars", "n_images", "n_big_words", "hl_n", "v_long",
         "v_small", "rooms", "n_scale_ratio", "vb_big", "vb_n", "img_big"]
RATIOS = ["img_cover", "draw_cover", "white", "sat", "edge", "axis", "entropy", "ink", "dark", "mid", "flat6", "ncol", "vivid", "soft",
          "hl_len", "hl_orth", "room_fill", "v_orth", "c_aspect", "c_share", "c_fill", "light", "hl_h", "hl_v", "hl_ent", "vb_max", "img_max",
          "text_cover"]


def _votes(text, fname):
    t = sp.norm(text)
    f = sp.norm(re.sub(r"[_\-.]+", " ", fname)) + " " + sp.norm(fname)
    tv, fv = [], []
    for typ in sp.TYPES:
        tv.append(math.log1p(len(re.findall(sp.KW[typ], t))) if typ in sp.KW else 0.0)
        fv.append(1.0 if typ in sp.FN_ONLY and re.search(sp.FN_ONLY[typ], f) else 0.0)
    return tv + fv


def names():
    return (STATS + RATIOS + ["aspect", "log_area", "text_density", "wall_max", "wall_classes", "heavy", "big_regions", "region_max",
                              "region_n", "dark_ink", "mid_ink", "log_c_aspect"] + ["kw_" + t for t in sp.TYPES] + ["fn_" + t for t in sp.TYPES])


def vector(f, fname):
    """Feature vector of one page (a features.page_features dict). Errors give None."""
    if f.get("error") or "ink" not in f:
        return None
    w, h = f["w_pt"], f["h_pt"]
    area = w * h
    v = [math.log1p(f.get(k, 0)) for k in STATS] + [float(f.get(k, 0)) for k in RATIOS]
    ws = sorted(f.get("widths") or [])
    regs = [r for r in f.get("regions", []) if (r[2] - r[0]) * (r[3] - r[1]) > 0.06 * area]
    rmax = max([(r[2] - r[0]) * (r[3] - r[1]) / area for r in f.get("regions", [])] or [0.0])
    v += [w / max(h, 1e-6), math.log(max(area, 1.0)), 1e4 * f["n_words"] / max(area, 1.0), ws[-1][0] if ws else 0.0,
          float(len(ws)), float(sp.heavy_walls(f.get("widths"))), float(len(regs)), rmax, float(len(f.get("regions", []))),
          f["dark"] / max(f["ink"], 1e-3), f["mid"] / max(f["ink"], 1e-3), math.log(max(f["c_aspect"], 1e-3))]
    v += _votes(f["text"], fname)
    return v


class Ensemble:
    """Mean of a standardised logistic regression and a random forest: the forest alone is overconfident on few labels."""

    def __init__(self):
        from sklearn.ensemble import RandomForestClassifier
        from sklearn.linear_model import LogisticRegression
        from sklearn.pipeline import make_pipeline
        from sklearn.preprocessing import StandardScaler
        self.lr = make_pipeline(StandardScaler(), LogisticRegression(C=0.2, max_iter=5000, class_weight="balanced"))
        self.rf = RandomForestClassifier(500, class_weight="balanced_subsample", random_state=1572)

    def fit(self, X, y):
        self.lr.fit(X, y)
        self.rf.fit(X, y)
        self.classes_ = self.lr.classes_
        return self

    def predict_proba(self, X):
        return 0.5 * self.lr.predict_proba(X) + 0.5 * self.rf.predict_proba(X)


def label_files():
    here = os.path.dirname(os.path.abspath(__file__))
    return [os.path.join(here, "sort_labels.csv"), os.path.join(here, "sort_labels_train.csv")]


def train(pages, labels):
    """Fit on the labelled pages (a list of feature dicts) -> an Ensemble."""
    X, y = [], []
    idx = {(p["path"], p["page"]): p for p in pages}
    for k, t in labels.items():
        p = idx.get(k)
        v = vector(p, os.path.basename(k[0])) if p else None
        if v is not None:
            X.append(v)
            y.append(t)
    return Ensemble().fit(np.array(X), np.array(y))


def load_or_train(pages, out):
    """The model trained on the label files, cached in out/_cache keyed on the label files and the feature version."""
    import hashlib
    import pickle
    import features
    h = hashlib.sha1(str(features.VERSION).encode())
    labels = {}
    for f in label_files():
        h.update(open(f, "rb").read())
        labels.update(sp.read_labels(f))
    path = os.path.join(out, "_cache", "sortmodel_%s.pkl" % h.hexdigest()[:12])
    try:
        with open(path, "rb") as fh:
            return pickle.load(fh)
    except Exception:
        import corpus_features as cf
        every = [it for k, v in cf.load_cache(out).items() if k.endswith("|v%d" % features.VERSION) for it in v]  # labels may be in other venues
        m = train(every or pages, labels)
        try:
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, "wb") as fh:
                pickle.dump(m, fh)
        except OSError:
            pass
        return m


def probabilities(m, f, fname):
    """{type: probability} of one page, or {} when it has no features."""
    v = vector(f, fname)
    if v is None:
        return {}
    return dict(zip((str(c) for c in m.classes_), m.predict_proba(np.array([v]))[0]))

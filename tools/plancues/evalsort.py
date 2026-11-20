"""Scores sorted pages against a hand-labelled CSV: accuracy, confusion matrix, per-class precision and recall.

    python -I tools/plancues/evalsort.py sort_labels_holdout.csv [--field type|rule_type|model_type|vlm_type] [--report out/_all/sort_report.csv]

The report is the CSV written by `plancues.py sort all`. Pages the model left unsure still count with their best guess.
"""
import argparse
import collections
import csv
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import sortpages as sp  # noqa: E402

GEOMETRY = ("floor_plan", "site_plan", "section")


def confusion(pairs):
    """pairs: (true, predicted). -> (matrix dict, accuracy)."""
    m = collections.Counter(pairs)
    n = sum(m.values())
    hit = sum(v for (t, p), v in m.items() if t == p)
    return m, hit / max(1, n)


def per_class(m):
    """{type: (precision, recall, n_true, n_pred)}"""
    out = {}
    for c in sp.TYPES:
        tp = m.get((c, c), 0)
        npred = sum(v for (t, p), v in m.items() if p == c)
        ntrue = sum(v for (t, p), v in m.items() if t == c)
        out[c] = (tp / npred if npred else float("nan"), tp / ntrue if ntrue else float("nan"), ntrue, npred)
    return out


def text_matrix(m):
    w = max(len(c) for c in sp.TYPES) + 1
    lines = [" " * w + " ".join("%4s" % c[:4] for c in sp.TYPES) + "  (rows true, columns predicted)"]
    for t in sp.TYPES:
        lines.append(t.ljust(w) + " ".join("%4d" % m.get((t, p), 0) for p in sp.TYPES))
    return "\n".join(lines)


def read_pred(report, field):
    """{(path, page): (type in `field`, unsure)}"""
    out = {}
    with open(report, newline="", encoding="utf-8") as f:
        for r in csv.DictReader(f):
            out[(r["path"], int(r["page"]))] = (r.get(field) or r["type"], r.get("unsure") == "True")
    return out


def read_alternatives(path):
    """{(path, page): second acceptable type} for labels the labeller marked ambiguous (column ambiguous_with)."""
    out = {}
    with open(path, newline="", encoding="utf-8") as f:
        for r in csv.DictReader(f):
            if r.get("ambiguous_with"):
                out[(r["path"], int(r["page"]))] = r["ambiguous_with"]
    return out


def main():
    a = argparse.ArgumentParser()
    a.add_argument("labels")
    a.add_argument("--report", default=os.path.join(os.environ.get("PLANCUES_OUT", "X:/UnitSportSwitzerland/plancues/out"), "_all", "sort_report.csv"))
    a.add_argument("--field", default="type")
    a = a.parse_args()
    lab = sp.read_labels(a.labels)
    pred = read_pred(a.report, a.field)
    pairs = [(t, pred[k][0]) for k, t in lab.items() if k in pred]
    m, acc = confusion(pairs)
    print("%s on %s: %d / %d = %.1f %%" % (a.field, os.path.basename(a.labels), sum(v for (t, p), v in m.items() if t == p), len(pairs), 100 * acc))
    print(text_matrix(m))
    for c, (p, r, nt, npred) in per_class(m).items():
        print("%-13s precision %5.1f %%  recall %5.1f %%  (true %d, predicted %d)" % (c, 100 * p, 100 * r, nt, npred))
    alt = read_alternatives(a.labels)
    if alt:
        ok = sum(1 for k, t in lab.items() if k in pred and (pred[k][0] == t or pred[k][0] == alt.get(k)))
        print("lenient (the second acceptable type of %d ambiguous pages counts): %d / %d = %.1f %%" % (len(alt), ok, len(pairs), 100 * ok / max(1, len(pairs))))
    sure = [(t, pred[k][0]) for k, t in lab.items() if k in pred and not pred[k][1]]
    if sure:
        ms, accs = confusion(sure)
        print("sure pages only: %d of %d pages, accuracy %.1f %%" % (len(sure), len(pairs), 100 * accs))
        for c in GEOMETRY:
            tp = ms.get((c, c), 0)
            npred = sum(v for (t, p), v in ms.items() if p == c)
            ntrue = sum(1 for k, t in lab.items() if k in pred and t == c)
            print("  %-10s sure precision %5.1f %% (%d of %d)  recall %5.1f %% (%d of %d)" % (c, 100 * tp / max(1, npred), tp, npred, 100 * tp / max(1, ntrue), tp, ntrue))


if __name__ == "__main__":
    main()

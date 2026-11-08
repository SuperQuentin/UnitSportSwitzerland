"""Batch commands over the scans of the venues (`python scancmd.py check|run`): is each scan placed on the venue's building, and what rooms does it give.

check  -> out/_all/scan_placement_check.csv/.jsonl: for every placed scan cue, the identity and aerial checks of `scancheck` and the verdict.
run    -> re-runs the raster stage with the checks and, for the placements that pass, the rooms of `scanrooms` (cues are rewritten, the old ones saved
          under `--backup`), then exports and runs the game's `--houselayoutcheck` (see `scanexport`).
"""
import argparse
import csv
import json
import os
import sys

import cue as cuem
import pdfio
import pipeline
import scancheck

IMG = (".jpg", ".jpeg", ".png", ".tif", ".tiff")


def scan_cues(out, venues):
    """[(venue, cue path, cue)] of the raster cues of `venues` that were placed on a building and not rejected."""
    rows = []
    for v in venues:
        for p in cuem.list_cues(out, v):
            c = cuem.load(p)
            if c["sources"][0]["kind"] == "raster" and c.get("placement") and c["status"] != "rejected":
                rows.append((v, p, c))
    return rows


def venues_from_status(path):
    """The venues the status table lists as an outline-only scan placement ('close' with 'outline only')."""
    with open(path, encoding="utf-8-sig") as f:
        return [r["venue"] for r in csv.DictReader(f) if r["category"] == "close" and "outline only" in r["quality"]]


def check_cue(c, chunks, gwr, cache_dir, info):
    """dict of the checks for one placed scan cue (does not change the cue)."""
    import tiles
    pl = c["placement"]
    b = scancheck.solid_of(chunks, pl)
    if b is None:
        return dict(verdict="wrong_building?", reason="the solid (EGID %s) is not in the tiles" % pl["egid"], identity=None, aerial=None, fit_iou_on_solid=None)
    poly = scancheck.placed_outline(c)
    iou = poly.intersection(b["fp"]).area / poly.union(b["fp"]).area
    ident = scancheck.identity(b["fp"], int(b["egid"]), info, gwr)
    try:
        aer = scancheck.aerial_check(poly, cache_dir, float(b["max_y"] - b["min_y"]))
    except Exception as e:
        aer = dict(score=None, null_p90=None, lift=None, tall=False, height_m=None, passes=False, error=repr(e))
    v, why = scancheck.verdict(ident, aer)
    return dict(verdict=v, reason=why, identity=ident, aerial=aer, fit_iou_on_solid=round(float(iou), 3))


def cmd_check(a):
    venues = a.venues or venues_from_status(os.path.join(a.out, "_all", a.status))
    rows = []
    for v, p, c in scan_cues(a.out, venues):
        r = check_cue(c, a.chunks, a.gwr, os.path.join(a.out, "_cache"), pipeline.venue_info(v))
        pl = c["placement"]
        row = dict(venue=v, cue=os.path.basename(p)[:-5], fit_iou=pl["iou"], iou_on_placed_solid=r["fit_iou_on_solid"], egid=pl["egid"], status=c["status"],
                   identity=(r["identity"] or {}).get("verdict"), identity_how=(r["identity"] or {}).get("how"), identity_dist_m=(r["identity"] or {}).get("dist_m"),
                   aerial_score=(r["aerial"] or {}).get("score"), aerial_null_p90=(r["aerial"] or {}).get("null_p90"), aerial_lift=(r["aerial"] or {}).get("lift"),
                   tall=(r["aerial"] or {}).get("tall"), height_m=(r["aerial"] or {}).get("height_m"), verdict=r["verdict"], reason=r["reason"], detail=r)
        rows.append(row)
        print("%-34s %-44s fit %.2f | %-11s %-8s | aerial %s vs %s%s | %s" % (v[:34], row["cue"][:44], pl["iou"], row["identity"], row["identity_how"], row["aerial_score"],
              row["aerial_null_p90"], " TALL" if row["tall"] else "", row["verdict"]), flush=True)
    keys = [k for k in rows[0] if k != "detail"] if rows else []
    with open(os.path.join(a.out, "_all", "scan_placement_check.csv"), "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, keys)
        w.writeheader()
        for r in rows:
            w.writerow({k: r[k] for k in keys})
    with open(os.path.join(a.out, "_all", "scan_placement_check.jsonl"), "w", encoding="utf-8") as f:
        for r in rows:
            f.write(json.dumps(r, ensure_ascii=False, default=float) + "\n")
    return rows


def main(argv=None):
    ap = argparse.ArgumentParser()
    ap.add_argument("what", choices=["check"])
    ap.add_argument("--out", default=pdfio.DEFAULT_OUT)
    ap.add_argument("--chunks", default=pdfio.DEFAULT_CHUNKS)
    ap.add_argument("--gwr", default=pdfio.DEFAULT_GWR)
    ap.add_argument("--status", default="venues_status.csv")
    ap.add_argument("--venues", nargs="*")
    a = ap.parse_args(argv)
    if a.what == "check":
        cmd_check(a)


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    main()

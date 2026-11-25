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

GODOT = "C:/ProgramData/chocolatey/lib/godot-mono/tools/godot_v4.7.2-stable_mono_win64/godot_v4.7.2-stable_mono_win64_console.exe"
WT = "C:/Users/Syra/projects/UnitSportSwitzerland-1574"  # the build of #1574 (BuildingOutline over every height), for --houselayoutcheck
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


def check_with_game(path, logp, a):
    """Run the game's `--houselayoutcheck` on an export; (RESULT line, reasons)."""
    import subprocess
    try:
        r = subprocess.run([a.godot, "--headless", "--path", a.worktree, "--", "--houselayoutcheck", path, "--chunks", a.chunks], capture_output=True, text=True,
                           timeout=300, encoding="utf-8", errors="replace")
        txt = r.stdout + r.stderr
    except subprocess.TimeoutExpired:
        txt = "TIMEOUT"
    os.makedirs(os.path.dirname(logp), exist_ok=True)
    with open(logp, "w", encoding="utf-8") as f:
        f.write(txt)
    res = [ln for ln in txt.splitlines() if "RESULT" in ln]
    why = [ln for ln in txt.splitlines() if ln.startswith("[houselayout]") and "RESULT" not in ln and "EGID" not in ln]
    return (res[-1] if res else "NO RESULT LINE"), " / ".join(why)[:300]


def overlay_layout(layout_path, out_png, px_per_m=12):
    """The exported layout as the game receives it (rooms, doors; layout metres, z down), drawn on white: the export render."""
    import cv2
    import numpy as np
    lay = json.load(open(layout_path, encoding="utf-8"))["layouts"][0]
    fl = lay["floors"][0]
    pts = [p for r in fl["rooms"] for p in r["points"]]
    xs, zs = [p[0] for p in pts], [p[1] for p in pts]
    x0, z0 = min(xs) - 3, min(zs) - 3
    w, h = int((max(xs) - x0 + 3) * px_per_m), int((max(zs) - z0 + 3) * px_per_m)
    img = np.full((h, w, 3), 255, np.uint8)

    def px(p):
        return [int(round((p[0] - x0) * px_per_m)), int(round((p[1] - z0) * px_per_m))]
    rng = np.random.RandomState(5)
    for r in fl["rooms"]:
        a = np.array([px(p) for p in r["points"]], np.int32)
        cv2.fillPoly(img, [a], tuple(int(v) for v in rng.randint(170, 245, 3)))
        cv2.polylines(img, [a], True, (60, 60, 60), 1, cv2.LINE_AA)
    for o in fl["openings"]:
        cv2.line(img, tuple(px((o["x0"], o["z0"]))), tuple(px((o["x1"], o["z1"]))), (0, 0, 220) if o.get("kind") == "door" else (200, 120, 0), 3, cv2.LINE_AA)
    os.makedirs(os.path.dirname(out_png), exist_ok=True)
    cv2.imwrite(out_png, img)
    return out_png


def run_job(job):
    """One scan cue: the raster stage again with the checks and rooms, then export, the game's check and the two overlays. Returns a row."""
    import argparse as ap2
    import contextlib
    import io
    import re
    import exportcmd
    import review
    a, venue, path = job["a"], job["venue"], job["path"]
    name = os.path.basename(path)[:-5]
    row = dict(venue=venue, cue=name)
    try:
        old = cuem.load(path)
        src = old["sources"][0]
        region = tuple(src["bbox"]) if re.search(r"_r\d+$", name) else None
        info = pipeline.venue_info(venue)
        c = pipeline.raster_stage(a.root, venue, src["path"], src["page"], a.chunks, info, region, gwr=a.gwr, cache_dir=os.path.join(a.out, "_cache"))
        cuem.save(path, c)
        chk = c.get("placement_check") or {}
        lvl = (c["levels"] or [{}])[0]
        row.update(status=c["status"], fit_iou=(c.get("placement") or {}).get("iou"), verdict=chk.get("verdict"), reason=chk.get("reason"),
                   rooms=len(lvl.get("rooms", [])), doors_scan_gap=len(lvl.get("openings", [])), scan_rooms=(lvl.get("extra") or {}).get("scan_rooms"))
        dest = os.path.join(a.out, venue)
        ov = os.path.join(a.out, "_all", "venue_overlays", "scan")
        if c.get("placement"):
            row["overlay_aerial"] = review.overlay_aerial(c, os.path.join(a.out, "_cache"), os.path.join(ov, "%s__%s_aerial.png" % (venue, name)))
        if lvl.get("rooms"):
            ns = ap2.Namespace(venue=venue, what="house", cue=name, out=a.out, root=a.root, chunks=a.chunks, gwr=a.gwr, exit_snap=0.0)
            buf = io.StringIO()
            with contextlib.redirect_stdout(buf):
                exportcmd.main(ns)
            repp = os.path.join(dest, "house-layouts_%s.report.json" % name)
            if os.path.exists(repp):
                rep = json.load(open(repp, encoding="utf-8"))
                row.update(rooms_kept=rep.get("rooms_kept"), rooms_in_cue=rep.get("rooms_in_cue"), doors_total=rep.get("doors"), doors_extracted=rep.get("doors_real"),
                           doors_inferred=rep.get("doors_inferred"), refused=rep.get("refused"), pyval=rep.get("python_validate"))
            ex = os.path.join(dest, "house-layouts_%s.json" % name)
            if os.path.exists(ex):
                row["export"] = ex
                row["check"], row["check_why"] = check_with_game(ex, os.path.join(dest, "check", "%s_houselayoutcheck.txt" % name), a)
                row["overlay_export"] = overlay_layout(ex, os.path.join(ov, "%s__%s_export.png" % (venue, name)))
            row["export_log"] = buf.getvalue()[-300:]
    except Exception as e:
        import traceback
        row.update(error=repr(e), tb=traceback.format_exc()[-500:])
    return row


def cmd_run(a):
    import shutil
    from concurrent.futures import ProcessPoolExecutor
    venues = a.venues or venues_from_status(os.path.join(a.out, "_all", a.status))
    jobs = []
    for v, p, c in scan_cues(a.out, venues):
        dst = os.path.join(a.backup, v, "cues")
        os.makedirs(dst, exist_ok=True)
        shutil.copy2(p, dst)
        jobs.append(dict(a=a, venue=v, path=p))
    print(len(jobs), "scan cues")
    rows = []
    with ProcessPoolExecutor(a.jobs) as ex, open(os.path.join(a.out, "_all", "scan_run.jsonl"), "w", encoding="utf-8") as f:
        for r in ex.map(run_job, jobs, chunksize=1):
            rows.append(r)
            f.write(json.dumps(r, ensure_ascii=False, default=float) + "\n")
            f.flush()
            print(r["venue"][:30], r["cue"][:40], r.get("verdict"), "rooms", r.get("rooms"), "kept", r.get("rooms_kept"), "doors", r.get("doors_extracted"), r.get("doors_inferred"),
                  r.get("check"), r.get("error", ""), flush=True)
    return rows


def main(argv=None):
    ap = argparse.ArgumentParser()
    ap.add_argument("what", choices=["check", "run", "status"])
    ap.add_argument("--root", default=pdfio.DEFAULT_ROOT)
    ap.add_argument("--out", default=pdfio.DEFAULT_OUT)
    ap.add_argument("--chunks", default=pdfio.DEFAULT_CHUNKS)
    ap.add_argument("--gwr", default=pdfio.DEFAULT_GWR)
    ap.add_argument("--status", default="venues_status.csv")
    ap.add_argument("--venues", nargs="*")
    ap.add_argument("--backup", default=os.path.join(pdfio.DEFAULT_OUT, "..", "backup", "pre-scan-rooms"))
    ap.add_argument("--jobs", type=int, default=4)
    ap.add_argument("--godot", default=GODOT)
    ap.add_argument("--worktree", default=WT)
    a = ap.parse_args(argv)
    if a.what == "check":
        cmd_check(a)
    elif a.what == "status":
        cmd_status(a)
    else:
        cmd_run(a)




SCAN_CATEGORIES = ["scan placed, export passes the game check", "scan placed, export fails the game check", "scan placed, no usable rooms",
                   "scan not confirmed (wrong_building?)"]


def cmd_status(a):
    """A second status table next to `venues_status.csv` (never overwritten): the scan venues re-judged by `check` and `run`, as `venues_status_scan.csv/.md`."""
    base = os.path.join(a.out, "_all")
    with open(os.path.join(base, a.status), encoding="utf-8-sig") as f:
        table = list(csv.DictReader(f))
    chk = {}
    for ln in open(os.path.join(base, "scan_placement_check.jsonl"), encoding="utf-8"):
        r = json.loads(ln)
        chk.setdefault(r["venue"], []).append(r)
    run = {}
    for ln in open(os.path.join(base, "scan_run.jsonl"), encoding="utf-8"):
        r = json.loads(ln)
        run.setdefault(r["venue"], []).append(r)
    out = []
    for r in table:
        v = r["venue"]
        r = dict(r, scan_check="", scan_cues_ok="", scan_best="", scan_category_new=r["category"])
        if v in chk:
            cs = chk[v]
            good = [c["cue"] for c in cs if c["verdict"].startswith("ok")]
            rr = run.get(v, [])
            passed = [x for x in rr if (x.get("check") or "").endswith("PASSED") or "RESULT: OK" in (x.get("check") or "")]
            written = [x for x in rr if x.get("export")]
            r["scan_check"] = "; ".join(sorted({c["verdict"] for c in cs}))
            r["scan_cues_ok"] = "%d of %d" % (len(good), len(cs))
            best = max(rr, key=lambda x: (x.get("rooms_kept") or 0, x.get("rooms") or 0), default=None)
            r["scan_best"] = "" if not best else "%s: %s rooms, %s kept, %s extracted + %s inferred doors, %s" % (
                best["cue"], best.get("rooms"), best.get("rooms_kept"), best.get("doors_extracted"), best.get("doors_inferred"), best.get("check") or best.get("refused") or "no export")
            if passed:
                r["scan_category_new"] = SCAN_CATEGORIES[0]
            elif written:
                r["scan_category_new"] = SCAN_CATEGORIES[1]
            elif good:
                r["scan_category_new"] = SCAN_CATEGORIES[2]
            else:
                r["scan_category_new"] = SCAN_CATEGORIES[3]
        out.append(r)
    keys = list(out[0])
    with open(os.path.join(base, "venues_status_scan.csv"), "w", newline="", encoding="utf-8-sig") as f:
        w = csv.DictWriter(f, keys)
        w.writeheader()
        w.writerows(out)
    from collections import Counter
    tot = Counter(r["scan_category_new"] for r in out)
    with open(os.path.join(base, "venues_status_scan.md"), "w", encoding="utf-8") as f:
        f.write("# Venue status with the scan placements re-judged\n\n`venues_status.csv` is untouched. New columns: scan_check, scan_cues_ok, scan_best, scan_category_new.\n\n| Category | Venues |\n|---|---|\n")
        for k, n in tot.most_common():
            f.write("| %s | %d |\n" % (k, n))
    print(dict(tot))
    return out


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    main()

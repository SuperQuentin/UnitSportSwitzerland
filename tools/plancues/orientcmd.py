"""`orient` subcommand: orientation plans (flat colour rooms, no scale bar) into cues, one page or every page the sort calls one."""
import csv
import json
import os

import cue as cuem
import footscale
import orient
import pipeline
import review


def run_page(a, venue, rel_path, page, region=None, review_it=True, cands=None, relax=False):
    """One page: extract, gate, place, save the cue, draw the review when placed. Returns a report row."""
    info = pipeline.venue_info(venue, at=[float(v) for v in a.at.split(",")] if getattr(a, "at", None) else None)
    name = cuem.cue_name(rel_path, page, region)
    thr = footscale.RELAXED if relax else None
    c, res, j = pipeline.orientation_stage(a.root, venue, rel_path, page, a.chunks, info, region=region, cands=cands,
                                           cache_dir=os.path.join(a.out, "_cache"), thr=thr)
    if relax:  # a drawing of a near miss, for looking only: never a cue file, never exported
        name += "_relaxed"
        d = os.path.join(a.out, "_all", "orient_review")
        cuem.save(os.path.join(d, name + ".json"), c)  # beside its drawings, outside any venue's cues
    else:
        cuem.save(cuem.cue_path(a.out, venue, name), c)
        d = os.path.join(a.out, venue)
    row = dict(venue=venue, path=rel_path, page=page, cue=name, status=c["status"], placed=bool(c.get("placement")),
               reasons=" | ".join(c["reasons"]))
    if res.get("rooms"):
        sm = orient.summary(res) if res.get("ok") else dict(rooms=len(res["rooms"]), labelled=0, named=0, names=[], marks={}, clusters=res.get("n_clusters"))
        row.update(rooms=sm["rooms"], labelled=sm["labelled"], named=sm["named"], names=";".join(n.replace("\n", " ") for n in sm["names"]),
                   marks=json.dumps(sm["marks"]), clusters=sm["clusters"])
    if j:
        row.update(gate_iou=None if j["iou"] is None else round(j["iou"], 3), gate_rms=None if j["rms_m"] is None else round(j["rms_m"], 2), egid=j["egid"])
    if c.get("placement") and review_it:
        row["aerial"] = review.overlay_aerial(c, os.path.join(a.out, "_cache"), os.path.join(d, name + "_aerial.png"))
        row["page_png"] = review.overlay_page(c, a.root, os.path.join(d, name + "_page.png"))
    return row


def pages_of_report(a):
    """Orientation plans of the full sort: (venue, path, page, unsure) of every non-duplicate page."""
    rep = os.path.join(a.out, "_all", "sort_report.csv")
    with open(rep, newline="", encoding="utf-8") as f:
        return [(r["venue"], r["path"], int(r["page"]), r["unsure"]) for r in csv.DictReader(f)
                if r["type"] == "orientation_plan" and not r["duplicate_of"]]


def main(a):
    import sys
    if a.venue != "all":
        import plancues
        rel_path = plancues.find_file(a.root, a.venue, a.file)
        row = run_page(a, a.venue, rel_path, a.page, a.region and plancues.region_bbox(a.root, a.out, rel_path, a.page, a.region), relax=a.relax)
        print(json.dumps(row, indent=1, ensure_ascii=False))
        return
    rows = []
    for venue, rel_path, page, unsure in pages_of_report(a):
        try:
            row = run_page(a, venue, rel_path, page)
        except Exception as e:  # one broken page must not stop the corpus
            row = dict(venue=venue, path=rel_path, page=page, status="error", reasons=repr(e))
        row["sort_unsure"] = unsure
        rows.append(row)
        print("%-9s %-34s p%-2d rooms %-3s named %-3s %s" % (row["status"], rel_path.split("/", 1)[1][-34:], page, row.get("rooms", "-"), row.get("named", "-"),
                                                          row["reasons"][:120]))
        sys.stdout.flush()
    cols = ["venue", "path", "page", "sort_unsure", "status", "placed", "rooms", "labelled", "named", "marks", "clusters", "gate_iou", "gate_rms", "egid",
            "cue", "aerial", "names", "reasons"]
    dest = os.path.join(a.out, "_all", "orientation_report.csv")
    with open(dest, "w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, cols, extrasaction="ignore")
        w.writeheader()
        w.writerows(rows)
    print("report:", dest)

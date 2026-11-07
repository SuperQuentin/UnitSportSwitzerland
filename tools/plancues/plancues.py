"""plancues: read architectural PDFs and images into 2D plan cues (#1572).

    python -I tools/plancues/plancues.py sort    <venue|all> [--vlm] [--labels sort_labels.csv]
    python -I tools/plancues/plancues.py scale   <venue> <file> <page> [--region K]
    python -I tools/plancues/plancues.py extract <venue> <file> <page> [--region K] [--kind building|stadium]
    python -I tools/plancues/plancues.py place   <venue> <cue>
    python -I tools/plancues/plancues.py export  <venue> house|stadium [--cue NAME]
    python -I tools/plancues/plancues.py review  <venue> [--cue NAME]
    python -I tools/plancues/plancues.py run     <venue> <file> <page> [--region K]     scale + extract + place + review

Pages are 1-based. Documents are read from --root (default X:/UnitSportSwitzerland), results go to --out
(default X:/UnitSportSwitzerland/plancues/out/<venue>/). Nothing is written into the repository.
"""
import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import cue as cuem  # noqa: E402
import pdfio  # noqa: E402


def _common(p):
    p.add_argument("--root", default=pdfio.DEFAULT_ROOT)
    p.add_argument("--out", default=pdfio.DEFAULT_OUT)
    p.add_argument("--chunks", default=pdfio.DEFAULT_CHUNKS)
    p.add_argument("--at", help="E,N position hint (default: venues.json)")
    p.add_argument("--kind", choices=["building", "stadium"])


def find_file(root, venue, name):
    docs = pdfio.list_documents(root, venue)
    hits = [d for d in docs if name in pdfio.rel(d, root)]
    if not hits:
        raise SystemExit("no document matching %r in %s" % (name, venue))
    return pdfio.rel(sorted(hits, key=len)[0], root)


def region_bbox(root, out, rel_path, page, region):
    if not region:
        return None
    import corpus_features as cf
    for items in cf.load_cache(out).values():
        for it in items:
            if it["path"] == rel_path and it["page"] == page and it.get("regions"):
                return tuple(it["regions"][region - 1])
    raise SystemExit("no region %d known for %s p%d: run sort first" % (region, rel_path, page))


def cmd_run(a, stages):
    import pipeline
    import place
    import review
    info = pipeline.venue_info(a.venue, at=[float(v) for v in a.at.split(",")] if a.at else None, kind=a.kind)
    rel_path = find_file(a.root, a.venue, a.file)
    box = region_bbox(a.root, a.out, rel_path, a.page, a.region)
    name = cuem.cue_name(rel_path, a.page, a.region)
    path = cuem.cue_path(a.out, a.venue, name)
    c, pg = pipeline.scale_stage(a.root, a.venue, rel_path, a.page, box, kinds=("pitch", "rink") if info["kind"] == "stadium" else ())
    print("scale:", c["status"], c["scale"] and (c["scale"]["method"], round(c["scale"]["m_per_pt"], 5)), c["reasons"])
    if c["scale"]:
        if "extract" in stages:
            dbg = os.path.join(a.out, a.venue, "debug_" + name)
            pipeline.extract_stage(c, pg, info["kind"], debug_dir=dbg)
            print("extract:", c["status"], c["reasons"], [(f["key"], f["value"]) for f in c["facts"]])
        if "place" in stages and c["levels"]:
            pipeline.place_stage(c, a.chunks, os.path.join(a.out, "_cache"), info)
            print("place:", c["status"], c["reasons"], c["placement"])
    cuem.save(path, c)
    print("cue:", path)
    if "review" in stages and c.get("placement"):
        d = os.path.join(a.out, a.venue)
        print("review:", review.overlay_aerial(c, os.path.join(a.out, "_cache"), os.path.join(d, name + "_aerial.png")))
        print("review:", review.overlay_page(c, a.root, os.path.join(d, name + "_page.png")))


def main(argv=None):
    ap = argparse.ArgumentParser(prog="plancues", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("sort")
    _common(p)
    p.add_argument("venue")
    p.add_argument("--vlm", action="store_true", help="let a local vision model vote on pages the rules are unsure of")
    p.add_argument("--labels", help="hand labels CSV (path,page,type) to score the sort against")
    p.add_argument("--only-unsure", action="store_true", help="with --labels: run the model only on labelled pages the rules are unsure of")
    for name in ("scale", "extract", "place", "run"):
        p = sub.add_parser(name)
        _common(p)
        p.add_argument("venue")
        p.add_argument("file")
        if name != "place":
            p.add_argument("page", type=int)
            p.add_argument("--region", type=int)
    p = sub.add_parser("export")
    _common(p)
    p.add_argument("venue")
    p.add_argument("what", choices=["house", "stadium"])
    p.add_argument("--cue", help="cue name (default: every cue of the venue)")
    p = sub.add_parser("review")
    _common(p)
    p.add_argument("venue")
    p.add_argument("--cue")
    a = ap.parse_args(argv)
    if a.cmd == "sort":
        import sortpages
        return sortpages.main(a)
    if a.cmd == "run":
        return cmd_run(a, ("extract", "place", "review"))
    if a.cmd in ("scale", "extract", "place"):
        return cmd_run(a, {"scale": (), "extract": ("extract",), "place": ("extract", "place")}[a.cmd])
    if a.cmd == "export":
        import exportcmd
        return exportcmd.main(a)
    if a.cmd == "review":
        import review
        for path in cuem.cue_list_for(a):
            pass


if __name__ == "__main__":
    sys.exit(main())

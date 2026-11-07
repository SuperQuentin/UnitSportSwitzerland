"""`footscale` subcommands: validate the footprint scale against pages that have a bar, and apply it to pages that have none.

    plancues.py footscale validate [--jobs N] [--types floor_plan,site_plan,mixed_sheet]
    plancues.py footscale apply    [--jobs N] [--all-sure]

Both read `out/_all/sort_report.csv` (run `sort all` first) and write a CSV next to it (`footscale_validation.csv`, `footscale_apply.csv`).
`validate` hides the bar: the page is scaled by the footprint alone (gate numbers recorded, not enforced) and compared with the bar.
"""
import csv
import json
import os
from concurrent.futures import ProcessPoolExecutor

import cue as cuem
import pdfio

MIN_REGION_PT2 = 4000.0  # smaller regions of a mixed sheet are thumbnails, not plans
PLAN_TYPES = ("floor_plan", "site_plan")


def _rows(out):
    with open(os.path.join(out, "_all", "sort_report.csv"), encoding="utf-8") as f:
        return list(csv.DictReader(f))


def _jobs(rows, types, sure_only, venues):
    jobs = []
    for r in rows:
        if r["type"] not in types or r["duplicate_of"] or int(r["n_paths"] or 0) <= 30 or r["venue"] not in venues:
            continue
        if sure_only and r["unsure"] != "False":
            continue
        regs = json.loads(r["regions"]) if r["regions"] else []
        if len(regs) > 1:
            for k, b in enumerate(regs, 1):
                if (b[2] - b[0]) * (b[3] - b[1]) >= MIN_REGION_PT2:
                    jobs.append((r["venue"], r["path"], int(r["page"]), k, tuple(b), r["type"], r["unsure"]))
        else:
            jobs.append((r["venue"], r["path"], int(r["page"]), None, None, r["type"], r["unsure"]))
    return jobs


def _validate_job(args):
    job, root, chunks = args
    venue, path, page, k, box, typ, unsure = job
    import pipeline
    import footscale
    out = dict(venue=venue, path=path, page=page, region=k or "", type=typ, unsure=unsure)
    try:
        info = pipeline.venue_info(venue)
        bar, _ = pipeline.scale_stage(root, venue, path, page, box)
        sc = bar["scale"]
        if not sc or sc["method"] != "scale_bar":
            out["note"] = "no scale bar"
            return out
        out["bar_m_per_pt"] = sc["m_per_pt"]
        out.update(_bar_placement(pipeline, venue, path, page, box, sc["m_per_pt"], info, chunks, root))
        c, pg, j = pipeline.footscale_stage(root, venue, path, page, chunks, info, region=box, refs="always", final=False)
        if j is None:
            out["note"] = "; ".join(c["reasons"])[:200]
            return out
        r = j["fit"]
        fpa = j["footprint_area_m2"]
        out.update(fp_m_per_pt=j["raw_m_per_pt"], err_pct=100 * (j["raw_m_per_pt"] / sc["m_per_pt"] - 1), iou=j["iou"], rms_m=j["rms_m"],
                   rms_frac=j["rms_m"] / fpa ** 0.5, margin=j["margin"], distinct=j.get("distinct"), balance=r["coverage"] - r["footprint_cover"],
                   plan_cover=r["coverage"], fp_cover=r["footprint_cover"], egid=j["egid"], fp_area_m2=fpa,
                   twin=j.get("runner_up_same_scale"), aspect_plan=(j.get("aspect") or {}).get("plan"),
                   aspect_fp=(j.get("aspect") or {}).get("footprint"), ratios=",".join(map(str, footscale.written_ratios(pg.words))),
                   gate=j["status"], why="; ".join(j["reasons"])[:300])
    except Exception as e:  # one bad page must not stop the batch
        out["note"] = "error: %r" % (e,)
    return out


def _bar_placement(pipeline, venue, path, page, box, m, info, chunks, root):
    """Is the bar's scale really this drawing's? The outline at the bar's scale against every nearby footprint: best IoU and rms."""
    import place
    from shapely.geometry import Polygon
    pg = pipeline.Page(root, path, page)
    c0 = cuem.new_cue(venue, path, page, box or (0, 0, pg.rect.width, pg.rect.height), "vector")
    c0["scale"] = dict(m_per_pt=m, method="scale_bar", cross_check=None)
    pipeline.extract_stage(c0, pg, "building", outline_only=True)
    if not c0["levels"] or not info.get("at"):
        return dict(bar_iou=0.0)
    poly = Polygon(c0["levels"][0]["outline"])
    best = None
    for b in pipeline.footprint_candidates(chunks, info["at"]):
        f = place.fit_footprint(poly, b["fp"], g=0.5, step=2.0)
        if best is None or f["iou"] > best[0]:
            best = (f["iou"], f["rms_m"], b["egid"])
    return dict(bar_iou=best[0], bar_rms_m=best[1], bar_egid=best[2]) if best else dict(bar_iou=0.0)


def _write(path, rows):
    keys = []
    for r in rows:
        keys += [k for k in r if k not in keys]
    with open(path, "w", encoding="utf-8", newline="") as f:
        w = csv.DictWriter(f, keys)
        w.writeheader()
        w.writerows(rows)
    return path


def _map(fn, items, jobs):
    if jobs <= 1:
        return [fn(i) for i in items]
    with ProcessPoolExecutor(jobs) as ex:
        return list(ex.map(fn, items, chunksize=1))


def validate(a):
    import pipeline
    rows = _rows(a.out)
    venues = pipeline.venues()
    jobs = _jobs(rows, tuple(a.types.split(",")), False, venues)
    print("validation candidates:", len(jobs))
    res = _map(_validate_job, [(j, a.root, a.chunks) for j in jobs], a.jobs)
    path = _write(os.path.join(a.out, "_all", "footscale_validation.csv"), res)
    print("written:", path)
    return path


def _apply_job(args):
    job, root, chunks, out_dir = args
    venue, path, page, k, box, typ, unsure = job
    import pipeline
    import review
    res = dict(venue=venue, path=path, page=page, region=k or "", type=typ)
    try:
        info = pipeline.venue_info(venue)
        c0, _ = pipeline.scale_stage(root, venue, path, page, box)
        if c0["scale"]:
            res.update(had_scale=c0["scale"]["method"])
            return res
        cache = os.path.join(out_dir, "_cache")
        c, pg, j = pipeline.footscale_stage(root, venue, path, page, chunks, info, region=box, refs=True, final=True, cache_dir=cache)
        name = cuem.cue_name(path, page, k)
        cuem.save(cuem.cue_path(out_dir, venue, name), c)
        res.update(cue=name, status=c["status"], reasons="; ".join(c["reasons"])[:300])
        if j:
            res.update(iou=j["iou"], rms_m=j["rms_m"], margin=j["margin"], distinct=j.get("distinct"), egid=j["egid"],
                       m_per_pt=(c["scale"] or {}).get("m_per_pt"), raw_m_per_pt=j["raw_m_per_pt"],
                       ratio=(j["cross_check"] or {}).get("ratio"), ratio_error=(j["cross_check"] or {}).get("error"))
        if c["status"] != "rejected" and c.get("placement"):
            d = os.path.join(out_dir, venue)
            res["aerial"] = review.overlay_aerial(c, cache, os.path.join(d, name + "_fp_aerial.png"))
            res["page_png"] = review.overlay_page(c, root, os.path.join(d, name + "_fp_page.png"))
    except Exception as e:
        res["reasons"] = "error: %r" % (e,)
        res["status"] = "error"
    return res


def apply(a):
    import pipeline
    rows = _rows(a.out)
    jobs = _jobs(rows, PLAN_TYPES, not a.all_types, pipeline.venues())
    print("pages to try:", len(jobs))
    res = _map(_apply_job, [(j, a.root, a.chunks, a.out) for j in jobs], a.jobs)
    path = _write(os.path.join(a.out, "_all", "footscale_apply.csv"), res)
    new = [r for r in res if r.get("status") in ("ok", "low_confidence")]
    print("newly scaled:", len(new), "of", len([r for r in res if "had_scale" not in r]), "without a scale")
    print("written:", path)
    return path


def draw(a):
    """Overlays of one page (or region) with the gate relaxed: for looking at a near miss or a pick. Not a result."""
    import footscale
    import pipeline
    import review
    row = next(r for r in _rows(a.out) if a.file in r["path"] and int(r["page"]) == a.page)
    regs = json.loads(row["regions"]) if row["regions"] else []
    box = tuple(regs[a.region - 1]) if a.region else None
    info = pipeline.venue_info(row["venue"])
    thr = footscale.RELAXED if a.relax else None
    c, pg, j = pipeline.footscale_stage(a.root, row["venue"], row["path"], a.page, a.chunks, info, region=box, thr=thr, final=True,
                                        cache_dir=os.path.join(a.out, "_cache"))
    name = cuem.cue_name(row["path"], a.page, a.region)
    d = os.path.join(a.out, "_all", "footscale_review")
    print(name, c["status"], c["reasons"])
    if c.get("placement"):
        print(review.overlay_aerial(c, os.path.join(a.out, "_cache"), os.path.join(d, name + "_aerial.png")))
        print(review.overlay_page(c, a.root, os.path.join(d, name + "_page.png")))


def main(a):
    if a.what == "draw":
        return draw(a)
    return {"validate": validate, "apply": apply}[a.what](a)

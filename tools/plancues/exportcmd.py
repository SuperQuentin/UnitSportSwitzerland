"""`export` and `review` subcommands: loop over a venue's cue files."""
import json
import os

import cue as cuem
import export
import pdfio
import pipeline
import review

CATALOG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "src", "Terrain", "Stadiums", "StadiumCatalog.cs")


def _cues(a):
    paths = cuem.list_cues(a.out, a.venue)
    if a.cue:
        paths = [p for p in paths if os.path.basename(p)[:-5] == a.cue]
    return paths


def _section(a, c):
    """Measurements from the venue's long section, scaled by this cue's roof length; None when there is none."""
    import features
    import section
    docs = [d for d in pdfio.list_documents(a.root, a.venue) if "section" in os.path.basename(d).lower() and d.lower().endswith(".pdf")]
    roof = export.c_fact(c, "roof_outer_size_m", None)
    if not docs or not roof:
        return None, None
    doc = pdfio.open_doc(docs[0])
    _, dr = features.vector_stats(doc[0])
    length = max(roof)
    return section.measure(dr, length), pdfio.rel(docs[0], a.root)


def main(a):
    dest = os.path.join(a.out, a.venue)
    info = pipeline.venue_info(a.venue)
    if a.what == "stadium":
        for p in _cues(a):
            c = cuem.load(p)
            if not c.get("placement") or "seating_front" not in (c["levels"] or [{}])[0].get("extra", {}):
                continue
            sec, secdoc = _section(a, c)
            fields = None
            if sec:
                fields = dict(RoofOuter=round(sec["roof_edge_m"], 1), RoofInner=round(sec["roof_top_m"], 1))
            text, diff, f = export.stadium_row(c, info, CATALOG, fields)
            extra = ["// status %s; sources: %s" % (c["status"], c["sources"][0]["path"])]
            if sec:
                st = sec.get("steps", {})
                extra.append("// long section (%s): scaled by the plan's roof length, %.4f m/pt (1:400 would be 0.1411); pitch level to roof %.1f m;"
                             " the end stand's stepped profile has %s steps, riser %.2f m, tread %.2f m (current Tiers: risers 0.40/0.48, rows 0.8 deep)"
                             % (secdoc, sec["m_per_pt"], sec["roof_top_m"], st.get("count"), st.get("riser_m", 0), st.get("tread_m", 0)))
            base = os.path.join(dest, "stadium_row")
            os.makedirs(dest, exist_ok=True)
            with open(base + ".cs.txt", "w", encoding="utf-8") as fh:
                fh.write("\n".join(extra) + "\n" + text + "\n")
            with open(base + ".diff", "w", encoding="utf-8") as fh:
                fh.write(diff)
            with open(base + ".json", "w", encoding="utf-8") as fh:
                json.dump(dict(fields=f, section=sec, cue=os.path.basename(p)), fh, indent=1, default=float)
            print("\n".join(extra))
            print(text)
            print(diff)
            print("written:", base + ".cs.txt / .diff / .json")
            return
        print("no placed stadium cue in", dest)
        return
    for p in _cues(a):
        c = cuem.load(p)
        if not c.get("placement") or c["placement"].get("egid") is None:
            continue
        lay, rep = export.house_layout(c, a.chunks, exit_snap=a.exit_snap)
        name = os.path.basename(p)[:-5]
        with open(os.path.join(dest, "house-layouts_%s.report.json" % name), "w", encoding="utf-8") as fh:
            json.dump(rep, fh, indent=1, default=float)
        print(name, "rooms kept %s of %s, doors %s, python validation: %s" % (rep.get("rooms_kept"), rep.get("rooms_in_cue"), rep.get("doors"), rep.get("python_validate")))
        if rep.get("refused"):
            print("  not written: refused, %s" % rep["refused"])
        elif lay and not rep.get("python_validate"):
            path = os.path.join(dest, "house-layouts_%s.json" % name)
            export.write_layouts(path, [lay])
            print("  written:", path)
        else:
            print("  not written: it does not validate")


def review_cmd(a):
    dest = os.path.join(a.out, a.venue)
    for p in _cues(a):
        c = cuem.load(p)
        name = os.path.basename(p)[:-5]
        print(name, review_png(review.overlay_aerial, c, a.out, dest, name, "_aerial.png"), review_page(c, a, dest, name))


def review_png(fn, c, out, dest, name, suffix):
    return fn(c, os.path.join(out, "_cache"), os.path.join(dest, name + suffix))


def review_page(c, a, dest, name):
    return review.overlay_page(c, a.root, os.path.join(dest, name + "_page.png"))

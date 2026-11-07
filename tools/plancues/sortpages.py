"""Sort every page (and image file) of a venue or of the whole corpus into plan types.

Rules first: keywords of the text layer and of the file name (FR/DE/IT/EN), path counts and stroke weights, image
coverage, how rectilinear and colourful a raster is. A page the rules are unsure of may be put to a local vision model
(--vlm), which only votes on the page type; its answers are cached by page content. Duplicates (the wayback folders
hold many near-identical access maps) are marked, never dropped from the report. A sheet with several drawings and
photos is split into regions by bounding box.
"""
import csv
import hashlib
import json
import os
import re
import unicodedata

import corpus_features as cf
import features
import pdfio

TYPES = ["floor_plan", "site_plan", "section", "elevation", "seating_plan", "access_map", "mixed_sheet", "photo", "text"]

KW = {
    "floor_plan": r"erdgeschoss|obergeschoss|untergeschoss|grundriss|rez[- ]de[- ]chauss|rez-de|\betage\b|sous-sol|floor ?plan|ground ?floor|first floor|"
                  r"\blevel \d|\bniveau\b|piano terra|pianta|planta|geschoss|\b\d\.? ?og\b|\bug\b|\brdc\b|plan du \d|plan de l.etage|"
                  r"grundrisse|plans? des|etage|stockwerk|plan \d|plan_\d|/plan|floor_plan|ground_floor",
    "section": r"schnitt|\bcoupe|section|sezione|querschnitt|laengsschnitt|longitudinal|profil",
    "elevation": r"ansicht|fassade|facade|elevation|\bvue\b|prospetto|aufriss|west_elev|view of",
    "site_plan": r"lageplan|situation|site ?plan|siteplan|arealplan|masterplan|plan masse|umgebung|gesamtplan|plan de situation|perimeter|"
                 r"situationsplan|areal|site_plan|plan general|planimetria|aerial|luftbild|campus",
    "seating_plan": r"sitzplan|seating|plan de salle|bestuhlung|sektor|tribune|tribuene|kategorie|saalplan|platzplan|parkett|sitzpl|stehpl|"
                    r"seat map|saalplaene|stadionplan|block",
    "access_map": r"anreise|so finden|wegbeschreibung|access|acces|accès|how to get|directions|oeffentliche verkehr|öffentliche verkehr|arriv|"
                  r"anfahrt|fahrplan|map\b|karte|parkplatz|parking|getting here|venue map|zufahrt|bahnhof|park.?and|itinerary|wayfinding|lageplan.*anreise",
}
FN_ONLY = {  # file name words that settle a type on their own
    "elevation": r"elevation|facade|fassade|ansicht",
    "section": r"section|schnitt|coupe|sezione",
    "site_plan": r"site_?plan|lageplan|situation|siteplan|perimeter",
    "access_map": r"access|anreise|karte|map\b|parking|directions",
    "seating_plan": r"seating|sitzplan|sitzplaene|bestuhlung",
    "floor_plan": r"floor|plan_\d|ground|grundriss|plans\b|geschoss",
}


def norm(s):
    s = unicodedata.normalize("NFKD", s.lower())
    return "".join(ch for ch in s if not unicodedata.combining(ch))


def keyword_votes(text, fname):
    votes = {}
    t = norm(text)
    f = norm(re.sub(r"[_\-.]+", " ", fname)) + " " + norm(fname)
    for typ, pat in KW.items():
        n = len(re.findall(pat, t))
        if n:
            votes[typ] = votes.get(typ, 0) + min(n, 3)
        if re.search(FN_ONLY.get(typ, "$^"), f):
            votes[typ] = votes.get(typ, 0) + 4
    return votes


def heavy_walls(w):
    """Does the weight table look like a cut plan: a distinct heavy dark class next to many thin ones?"""
    if not w:
        return False
    ws = sorted(w)
    return len(ws) >= 3 and ws[-1][0] >= 0.3 and ws[-1][1] >= 15


def classify(f, fname):
    """(type, unsure, reason) for one page's features."""
    if f.get("error"):
        return "text", True, f["error"]
    votes = keyword_votes(f["text"], fname)
    paths, words = f["n_paths"], f["n_words"]
    page_area = f["w_pt"] * f["h_pt"]
    img_cover = f["img_cover"]
    photo_like = f["sat"] > 0.12 or f["white"] < 0.5
    rect_like = f["axis"] > 0.55 and f["white"] > 0.6
    named = max(votes, key=votes.get) if votes else None
    big_regions = [r for r in f.get("regions", []) if (r[2] - r[0]) * (r[3] - r[1]) > 0.06 * page_area]
    # a page of running text, perhaps with pictures
    if words > 150 and paths < 400 and f["draw_cover"] < 0.45:
        if len(big_regions) >= 2 or f["n_images"] >= 2:
            return "mixed_sheet", False, "text with several pictures or drawings"
        return "text", False, "running text"
    # photographs and scans: an image fills the page, nothing vector on it
    if img_cover > 0.6 and paths < 40:
        if words > 60 and f["white"] <= 0.7:
            return "mixed_sheet", True, "scanned page of text and pictures"
        if named and votes[named] >= 4 and named not in ("floor_plan",):
            return named, False, "raster drawing, named by %s" % ("text" if f["text"] else "file name")
        if photo_like and not rect_like:
            return "photo", f["sat"] < 0.15, "image fills the page, colourful"
        if named and votes[named] >= 4:
            return named, False, "raster drawing, named by %s" % ("text" if f["text"] else "file name")
        if rect_like:
            return "floor_plan", True, "raster drawing, rectilinear, nothing names its type"
        return "photo", True, "image fills the page, no text, not clearly a drawing"
    if paths < 40 and img_cover >= 0.12 and words < 80:
        return "photo", True, "a picture and little text"
    if paths < 40:
        return "text", False, "few paths, text page" if words > 20 else "almost empty page"
    if len(big_regions) >= 2 and paths > 200:
        return "mixed_sheet", False, "%d drawing/photo regions on one sheet" % len(big_regions)
    if paths < 120 and img_cover > 0.25:
        return ("photo" if photo_like else "text"), True, "few paths, images present"
    # a vector drawing: the words say what it is
    if votes:
        order = sorted(votes.items(), key=lambda kv: -kv[1])
        if votes.get("access_map") and votes.get("site_plan", 0) <= votes["access_map"] + 4 and not heavy_walls(f["widths"]):
            votes["access_map"] += 4  # a site plan that talks about how to get there is a map for visitors
            order = sorted(votes.items(), key=lambda kv: -kv[1])
        top, n = order[0]
        second = order[1][1] if len(order) > 1 else 0
        # a floor plan needs walls: do not believe a floor-plan keyword on a map-like page
        if top == "floor_plan" and not heavy_walls(f["widths"]) and f["sat"] > 0.1:
            return "access_map" if votes.get("access_map") else "site_plan", True, "plan keywords but no cut walls, colourful"
        return top, n - second < 2 or n < 3, "keywords %s" % dict(order[:3])
    # no words: judge by the drawing itself
    if words < 10 and heavy_walls(f["widths"]) and f["sat"] < 0.1:
        return "floor_plan", True, "cut walls (heavy weight class), no text"
    if f["sat"] > 0.12:
        return "access_map", True, "coloured vector map, no text"
    return "site_plan", True, "vector drawing, nothing names its type"


def collect_pages(root, out, venue, log=print):
    pages = list(cf.collect(root, out, venue, log=lambda s: None))
    return pages


def dedupe(pages, thresh=8):
    """Mark near-identical pages (same venue): duplicate_of = key of the first one."""
    seen = []
    for p in pages:
        p["duplicate_of"] = ""
        if p.get("error") or "dhash" not in p:
            continue
        h = int(p["dhash"], 16)
        for q in seen:
            if q["venue"] == p["venue"] and abs(q["w_pt"] / q["h_pt"] - p["w_pt"] / p["h_pt"]) < 0.05 and features.hamming(h, q["_h"]) <= thresh:
                p["duplicate_of"] = "%s#%d" % (q["path"], q["page"])
                break
        else:
            p["_h"] = h
            seen.append(p)
    return pages


def read_labels(path):
    out = {}
    with open(path, newline="", encoding="utf-8") as f:
        for r in csv.DictReader(f):
            out[(r["path"], int(r["page"]))] = r["type"]
    return out


def run(root, out, venue, vlm=False, labels=None, only_unsure=False, log=print):
    pages = collect_pages(root, out, venue)
    for p in pages:
        p["venue"] = pdfio.venue_of(os.path.join(root, p["path"]), root)
        fname = os.path.basename(p["path"])
        p["type"], p["unsure"], p["reason"] = classify(p, fname)
        p["rule_type"] = p["type"]
    dedupe(pages)
    if vlm:
        import vlm as vlmmod
        vlmmod.vote(pages, root, out, labels=labels, only_unsure=only_unsure, log=log)
    dest = os.path.join(out, venue if venue != "all" else "_all")
    os.makedirs(dest, exist_ok=True)
    path = os.path.join(dest, "sort_report.csv")
    cols = ["venue", "path", "page", "type", "rule_type", "unsure", "reason", "duplicate_of", "regions", "n_paths", "n_words", "img_cover", "w_pt", "h_pt"]
    try:
        fh = open(path, "w", newline="", encoding="utf-8")
    except OSError:
        fh = open(path + ".new", "w", newline="", encoding="utf-8")
    with fh:
        w = csv.writer(fh)
        w.writerow(cols)
        for p in pages:
            w.writerow([p.get(c, "") if c != "regions" else json.dumps(p.get("regions", [])) for c in cols])
    return pages, path


def score(pages, labels, field="type"):
    hit = tot = 0
    wrong = []
    idx = {(p["path"], p["page"]): p for p in pages}
    for k, t in labels.items():
        p = idx.get(k)
        if p is None:
            continue
        tot += 1
        if p.get(field) == t:
            hit += 1
        else:
            wrong.append((k, t, p.get(field)))
    return hit, tot, wrong


def main(a):
    labels = read_labels(a.labels) if a.labels else None
    pages, path = run(a.root, a.out, a.venue, vlm=a.vlm, labels=labels, only_unsure=a.only_unsure)
    from collections import Counter
    print("pages:", len(pages), Counter(p["type"] for p in pages))
    print("duplicates:", sum(1 for p in pages if p["duplicate_of"]), "errors:", [(p["path"], p["error"]) for p in pages if p.get("error")])
    print("report:", path)
    if labels:
        hit, tot, wrong = score(pages, labels, "rule_type")
        print("rules only: %d / %d = %.1f %%" % (hit, tot, 100 * hit / max(1, tot)))
        if a.vlm:
            hit2, tot2, wrong2 = score(pages, labels, "type")
            print("with model: %d / %d = %.1f %%" % (hit2, tot2, 100 * hit2 / max(1, tot2)))
        for k, t, g in wrong:
            print("  rules wrong: %s p%d: labelled %s, rules said %s" % (k[0], k[1], t, g))

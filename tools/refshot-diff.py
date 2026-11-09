#!/usr/bin/env python3
"""Compare two reference screenshot libraries (#1429, tools/refshots.sh).

    tools/refshot-diff.py A B [--out DIR] [--noise NOISE.json] [--write-noise NOISE.json]
                          [--scale N] [--expect FILE] [--sheets] [--all-sheets]

A and B are library folders (<root>/<tag>), with <style>/<view>.png and <style>/models/*.png under
them. Every picture both have is compared pixel by pixel:
    mean    mean absolute difference, 0-255, over the three channels
    changed share of pixels where some channel differs by more than 8/255
    max     the largest channel difference
Each shot is judged against a threshold for its changed share: its noise floor (--noise, written
by --write-noise from two runs of the same commit) times 2 plus 0.2 %, or 0.5 % without one.
Writes DIR (default B/diff-<A's tag>) with a heatmap and an A | B | diff composite per shot over
its threshold, report.html (grouped by the view's class from tests/refshots/views.txt, models apart)
and summary.md. Exit code 1 if a shot is over its threshold, else 0.
--write-noise records A against B as the noise floor (two runs of one commit) and exits 0.

Two different worlds (G2 C6, #1500: --frame earth against the flat library):
--scale N    compares both pictures box-shrunk N times (8: 200 x 112 cells of a 1600 x 900 shot), so a
             pattern drawn at another phase (the leaves' tile patterns, #1512) does not count and a wrong
             colour, a missing forest or a hole does. The noise floor must be written at the same scale.
--expect F   the differences expected on purpose (tests/refshots/earth-vs-flat.txt): a "judge" line takes a
             view (or class:<c>) out of the pass/fail count into "judged by eye", a "mask x0,y0,x1,y1" line
             (fractions of the picture) leaves that part out of the numbers. Each line says why.
--sheets     a contact sheet per style, sheet-<style>.png: A | B | difference for every view, grouped by class
             (models left out); --all-sheets also a composite for every shot, not only those over.
summary.md lists the shots within their floor, the ones over it and the judged ones, each with its number.
"""
import fnmatch
import html
import json
import os
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont

STEP = 8           # a channel difference above this counts as a changed pixel
DEFAULT = 0.005    # changed share allowed without a noise floor
HERE = os.path.dirname(os.path.abspath(__file__))
VIEWS = os.path.join(HERE, "..", "tests", "refshots", "views.txt")
ORDER = {"near": 0, "mid": 1, "far": 2, "edge": 3, "night": 4, "space": 5, "other": 6, "models": 7}


def classes():
    """{view name: class} from the catalogue."""
    out = {}
    try:
        with open(VIEWS, encoding="utf-8") as f:
            for line in f:
                parts = line.split()
                if parts and not parts[0].startswith("#") and len(parts) > 1:
                    out[parts[0]] = parts[1]
    except OSError:
        pass
    return out


def expectations(path):
    """[(pattern, verdict, rects, reason)] from an --expect file: "<view|class:c|style/view> judge|mask [x0,y0,x1,y1 ...] -- why"."""
    out = []
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            head, _, why = line.partition("--")
            p = head.split()
            if len(p) < 2 or p[1] not in ("judge", "mask"):
                print(f"[refshot-diff] {path}: not '<view> judge|mask ...': {line}", file=sys.stderr)
                continue
            rects = [tuple(float(c) for c in r.split(",")) for r in p[2:]]
            out.append((p[0], p[1], rects, why.strip()))
    return out


def matches(pattern, rel, view, cls):
    if pattern.startswith("class:"):
        return fnmatch.fnmatch(cls, pattern[6:])
    return fnmatch.fnmatch(view, pattern) or fnmatch.fnmatch(rel[:-4], pattern)


def pictures(root):
    """{relative path: absolute path} of every PNG under root, the diff folders left out."""
    found = {}
    for d, dirs, files in os.walk(root):
        dirs[:] = [x for x in dirs if not x.startswith("diff")]
        for f in files:
            if f.lower().endswith(".png") and not f.startswith("sheet-"):
                full = os.path.join(d, f)
                found[os.path.relpath(full, root).replace("\\", "/")] = full
    return found


def shrink(a, n):
    """Box-shrink an H x W x 3 array n times (the edge rows that do not fill a box dropped)."""
    if n <= 1:
        return a
    h, w = a.shape[0] // n * n, a.shape[1] // n * n
    return a[:h, :w].reshape(h // n, n, w // n, n, 3).mean(axis=(1, 3))


def compare(a_path, b_path, scale=1, masks=()):
    a = np.asarray(Image.open(a_path).convert("RGB"), dtype=np.float32)
    b = np.asarray(Image.open(b_path).convert("RGB"), dtype=np.float32)
    if a.shape != b.shape:
        return None, None, None
    full = np.abs(a - b).max(axis=2)
    d = np.abs(shrink(a, scale) - shrink(b, scale))
    per_cell = d.max(axis=2)
    keep = np.ones(per_cell.shape, dtype=bool)
    hh, ww = keep.shape
    for x0, y0, x1, y1 in masks:
        keep[int(y0 * hh):int(np.ceil(y1 * hh)), int(x0 * ww):int(np.ceil(x1 * ww))] = False
    if not keep.any():
        keep[:] = True
    stats = {
        "mean": float(d[keep].mean()),
        "changed": float((per_cell[keep] > STEP).mean()),
        "max": int(per_cell[keep].max()),
    }
    return stats, full, keep


def heat(per_pixel, keep=None):
    """Black where equal, yellow to red as the difference grows; masked parts grey."""
    v = np.clip(per_pixel.astype(np.float32) / 64.0, 0, 1)
    rgb = np.stack([np.where(v > 0, 255, 0), (255 * (1 - v)) * (v > 0), np.zeros_like(v)], axis=2)
    if keep is not None and not keep.all():
        k = np.asarray(Image.fromarray(keep.astype(np.uint8) * 255).resize(
            (per_pixel.shape[1], per_pixel.shape[0]), Image.NEAREST)) > 0
        rgb[~k] = rgb[~k] * 0.25 + 60
    return Image.fromarray(rgb.astype(np.uint8))


def composite(a_path, b_path, per_pixel, out_path, keep=None):
    a, b, h = Image.open(a_path).convert("RGB"), Image.open(b_path).convert("RGB"), heat(per_pixel, keep)
    w, ht = a.size
    sheet = Image.new("RGB", (w * 3, ht))
    for i, im in enumerate((a, b, h)):
        sheet.paste(im, (i * w, 0))
    # half size: three 1600-wide pictures side by side are big enough at 2400
    sheet = sheet.resize((w * 3 // 2, ht // 2), Image.LANCZOS)
    sheet.save(out_path, optimize=True)


def font(size):
    try:
        return ImageFont.load_default(size=size)
    except TypeError:
        return ImageFont.load_default()


def contact_sheet(style, rows, a_pics, b_pics, maps, a_name, b_name, out_path, tw=480):
    """A | B | difference for every view of one style, grouped by class, each row labelled with its verdict."""
    rows = [r for r in rows if r["rel"].startswith(style + "/") and r["group"] != "models" and r["stats"]]
    if not rows:
        return False
    th = tw * 9 // 16
    label, gap, head = 30, 10, 64
    groups = sorted({r["group"] for r in rows}, key=lambda g: ORDER.get(g, 6))
    height = head + sum(40 + sum(label + th + gap for r in rows if r["group"] == g) for g in groups)
    sheet = Image.new("RGB", (tw * 3 + gap * 4, height), (17, 17, 17))
    d = ImageDraw.Draw(sheet)
    big, small = font(26), font(18)
    d.text((gap, 14), f"{style}: A {a_name} | B {b_name} | difference", fill=(230, 230, 230), font=big)
    y = head
    colour = {"over": (255, 110, 110), "within": (130, 220, 130), "judged": (240, 200, 90)}
    for g in groups:
        d.text((gap, y + 6), g.upper(), fill=(140, 200, 255), font=big)
        y += 40
        for r in [r for r in rows if r["group"] == g]:
            s = r["stats"]
            txt = (f"{os.path.basename(r['rel'])[:-4]}   {r['verdict']}   changed {s['changed'] * 100:.2f} % "
                   f"(limit {r['limit'] * 100:.2f} %)   mean {s['mean']:.1f}" + (f"   {r['why']}" if r["why"] else ""))
            d.text((gap, y + 5), txt[:150], fill=colour[r["verdict"]], font=small)
            y += label
            ims = [Image.open(a_pics[r["rel"]]).convert("RGB"), Image.open(b_pics[r["rel"]]).convert("RGB"),
                   heat(*maps[r["rel"]])]
            for i, im in enumerate(ims):
                sheet.paste(im.resize((tw, th), Image.LANCZOS), (gap + i * (tw + gap), y))
            y += th + gap
    sheet.save(out_path, optimize=True)
    return True


def main(argv):
    flags = {"--sheets", "--all-sheets"}
    args, opts, i = [], {}, 1
    while i < len(argv):
        a = argv[i]
        if a in flags:
            opts[a] = True
        elif a.startswith("--"):
            opts[a] = argv[i + 1] if i + 1 < len(argv) else ""
            i += 1
        else:
            args.append(a)
        i += 1
    if len(args) < 2:
        print(__doc__)
        return 2
    a_root, b_root = args[0].rstrip("/\\"), args[1].rstrip("/\\")
    a_name, b_name = os.path.basename(a_root), os.path.basename(b_root)
    a_pics, b_pics = pictures(a_root), pictures(b_root)
    both = sorted(a_pics.keys() & b_pics.keys())
    scale = int(opts.get("--scale", 1))
    noise = {}
    if "--noise" in opts:
        with open(opts["--noise"], encoding="utf-8") as f:
            noise = json.load(f)
    expect = expectations(opts["--expect"]) if "--expect" in opts else []
    cls = classes()

    def expected(rel):
        view = os.path.splitext(os.path.basename(rel))[0]
        group = "models" if "/models/" in rel else cls.get(view, "other")
        judge, masks = None, []
        for pat, verdict, rects, why in expect:
            if matches(pat, rel, view, group):
                if verdict == "judge" and judge is None:
                    judge = why or "judged by eye"
                elif verdict == "mask":
                    masks += rects
        return view, group, judge, masks

    results = {}
    for rel in both:
        _, _, _, masks = expected(rel)
        results[rel] = compare(a_pics[rel], b_pics[rel], scale, masks)

    if "--write-noise" in opts:
        floor = {rel: s["changed"] for rel, (s, _, _) in results.items() if s}
        with open(opts["--write-noise"], "w", encoding="utf-8") as f:
            json.dump(floor, f, indent=1, sort_keys=True)
        worst = sorted(floor.items(), key=lambda kv: -kv[1])[:10]
        print(f"noise floor of {len(floor)} shots written to {opts['--write-noise']} (scale {scale}); noisiest:")
        for rel, c in worst:
            print(f"  {c * 100:6.2f} %  {rel}")
        return 0

    out = opts.get("--out") or os.path.join(b_root, "diff-" + a_name)
    os.makedirs(out, exist_ok=True)
    rows, maps = [], {}
    for rel in both:
        stats, per_pixel, keep = results[rel]
        view, group, judge, masks = expected(rel)
        if stats is None:
            rows.append({"group": group, "rel": rel, "stats": None, "limit": 0, "verdict": "over", "why": "picture sizes differ",
                         "sheet": None})
            continue
        limit = noise[rel] * 2 + 0.002 if rel in noise else DEFAULT
        over = stats["changed"] > limit
        verdict = "judged" if judge else ("over" if over else "within")
        sheet = None
        if over or "--all-sheets" in opts or (group != "models" and stats["changed"] > 0):
            sheet = rel.replace("/", "__").replace(".png", ".diff.png")
            composite(a_pics[rel], b_pics[rel], per_pixel, os.path.join(out, sheet), keep)
        maps[rel] = (per_pixel, keep)
        rows.append({"group": group, "rel": rel, "stats": stats, "limit": limit, "verdict": verdict,
                     "why": judge or ("masked: " + " ".join(",".join(f"{c:g}" for c in r) for r in masks) if masks else ""),
                     "sheet": sheet})
    n_over = sum(r["verdict"] == "over" for r in rows)
    n_judged = sum(r["verdict"] == "judged" for r in rows)

    only_a = sorted(a_pics.keys() - b_pics.keys())
    only_b = sorted(b_pics.keys() - a_pics.keys())
    rows.sort(key=lambda r: (ORDER.get(r["group"], 6), -(r["stats"]["changed"] if r["stats"] else 1), r["rel"]))
    groups = sorted({r["group"] for r in rows}, key=lambda g: ORDER.get(g, 6))

    sheets = []
    if "--sheets" in opts:
        for style in sorted({r["rel"].split("/")[0] for r in rows}):
            p = os.path.join(out, f"sheet-{style}.png")
            if contact_sheet(style, rows, a_pics, b_pics, maps, a_name, b_name, p):
                sheets.append(p)

    def pct(r):
        return f"{r['stats']['changed'] * 100:.2f} %" if r["stats"] else "size differs"

    # summary.md, for a PR or an issue
    md = [f"# Reference screenshots: `{a_name}` vs `{b_name}`", "",
          f"{len(both)} shots compared" + (f" at 1/{scale} scale" if scale > 1 else "")
          + f": **{n_over} over threshold**, {len(rows) - n_over - n_judged} within, {n_judged} judged by eye"
          + (f"; {len(only_a)} only in A, {len(only_b)} only in B" if only_a or only_b else ""), "",
          "| class | shots | within | over | judged | worst changed | worst shot |", "|---|---|---|---|---|---|---|"]
    for g in groups:
        rs = [r for r in rows if r["group"] == g]
        worst = max(rs, key=lambda r: r["stats"]["changed"] if r["stats"] else 1)
        md.append(f"| {g} | {len(rs)} | {sum(r['verdict'] == 'within' for r in rs)} | {sum(r['verdict'] == 'over' for r in rs)} "
                  f"| {sum(r['verdict'] == 'judged' for r in rs)} | {pct(worst)} | `{worst['rel']}` |")
    if expect or scale > 1:
        def listing(title, verdict, note=False):
            rs = [r for r in rows if r["verdict"] == verdict and r["group"] != "models"]
            md.extend(["", f"## {title} ({len(rs)})", ""])
            if not rs:
                md.append("none")
            for g in groups:
                gs = [r for r in rs if r["group"] == g]
                if gs:
                    md.append(f"- **{g}**: " + ", ".join(
                        f"`{r['rel'][:-4]}` {pct(r)}" + (f" ({r['why']})" if note and r["why"] else "") for r in gs))
        listing("Over the floor: need a human look", "over")
        listing("Judged by eye: expected differences", "judged", note=True)
        listing("Within the floor", "within")
    if only_a or only_b:
        md.extend(["", "## Missing", ""] + [f"- only in A: `{r}`" for r in only_a] + [f"- only in B: `{r}`" for r in only_b])
    with open(os.path.join(out, "summary.md"), "w", encoding="utf-8") as f:
        f.write("\n".join(md) + "\n")

    # report.html, to look at
    h = ["<!doctype html><meta charset=utf-8><title>refshot diff</title>",
         "<style>body{font:14px system-ui;margin:16px;background:#111;color:#ddd}"
         "table{border-collapse:collapse}td,th{padding:3px 8px;border-bottom:1px solid #333}"
         ".over{color:#f66;font-weight:bold}.judged{color:#ec6}img{max-width:100%;margin:6px 0 18px}a{color:#8cf}</style>",
         f"<h1>{html.escape(a_name)} vs {html.escape(b_name)}</h1>",
         f"<p>{len(both)} shots" + (f" at 1/{scale} scale" if scale > 1 else "")
         + f", <b>{n_over} over threshold</b>, {n_judged} judged by eye. Each sheet: A | B | difference (yellow small, red large, masked grey).</p>"]
    for p in sheets:
        h.append(f"<p><a href='{html.escape(os.path.basename(p))}'>{html.escape(os.path.basename(p))}</a></p>")
    for g in groups:
        h.append(f"<h2>{html.escape(g)}</h2><table><tr><th>shot</th><th>verdict</th><th>changed</th><th>limit</th><th>mean</th><th>max</th><th>why</th></tr>")
        for r in rows:
            if r["group"] != g:
                continue
            name = f"<a href='#{html.escape(r['rel'])}'>{html.escape(r['rel'])}</a>" if r["sheet"] else html.escape(r["rel"])
            if r["stats"] is None:
                h.append(f"<tr class=over><td>{name}</td><td colspan=6>picture sizes differ</td></tr>")
                continue
            s = r["stats"]
            h.append(f"<tr class={r['verdict']}><td>{name}</td><td>{r['verdict']}</td><td>{s['changed'] * 100:.3f} %</td>"
                     f"<td>{r['limit'] * 100:.2f} %</td><td>{s['mean']:.2f}</td><td>{s['max']}</td><td>{html.escape(r['why'])}</td></tr>")
        h.append("</table>")
        for r in rows:
            if r["group"] == g and r["sheet"]:
                h.append(f"<h3 id='{html.escape(r['rel'])}'>{html.escape(r['rel'])}</h3><img src='{html.escape(r['sheet'])}'>")
    if only_a or only_b:
        h.append("<h2>missing</h2><ul>" + "".join(f"<li>only in A: {html.escape(r)}" for r in only_a)
                 + "".join(f"<li>only in B: {html.escape(r)}" for r in only_b) + "</ul>")
    with open(os.path.join(out, "report.html"), "w", encoding="utf-8") as f:
        f.write("\n".join(h))

    print("\n".join(md))
    print(f"\nreport: {os.path.join(out, 'report.html')}")
    for p in sheets:
        print(f"sheet: {p}")
    return 1 if n_over else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))

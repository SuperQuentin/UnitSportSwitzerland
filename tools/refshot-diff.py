#!/usr/bin/env python3
"""Compare two reference screenshot libraries (#1429, tools/refshots.sh).

    tools/refshot-diff.py A B [--out DIR] [--noise NOISE.json] [--write-noise NOISE.json]

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
"""
import html
import json
import os
import sys

import numpy as np
from PIL import Image

STEP = 8           # a channel difference above this counts as a changed pixel
DEFAULT = 0.005    # changed share allowed without a noise floor
VIEWS = os.path.join(os.path.dirname(__file__), "..", "tests", "refshots", "views.txt")


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


def pictures(root):
    """{relative path: absolute path} of every PNG under root, the diff folders left out."""
    found = {}
    for d, dirs, files in os.walk(root):
        dirs[:] = [x for x in dirs if not x.startswith("diff")]
        for f in files:
            if f.lower().endswith(".png"):
                full = os.path.join(d, f)
                found[os.path.relpath(full, root).replace("\\", "/")] = full
    return found


def compare(a_path, b_path):
    a = np.asarray(Image.open(a_path).convert("RGB"), dtype=np.int16)
    b = np.asarray(Image.open(b_path).convert("RGB"), dtype=np.int16)
    if a.shape != b.shape:
        return None, None
    d = np.abs(a - b)
    per_pixel = d.max(axis=2)
    stats = {
        "mean": float(d.mean()),
        "changed": float((per_pixel > STEP).mean()),
        "max": int(per_pixel.max()),
    }
    return stats, per_pixel


def heat(per_pixel):
    """Black where equal, yellow to red as the difference grows."""
    v = np.clip(per_pixel.astype(np.float32) / 64.0, 0, 1)
    rgb = np.stack([np.where(v > 0, 255, 0), (255 * (1 - v)) * (v > 0), np.zeros_like(v)], axis=2)
    return Image.fromarray(rgb.astype(np.uint8))


def composite(a_path, b_path, per_pixel, out_path):
    a, b, h = Image.open(a_path).convert("RGB"), Image.open(b_path).convert("RGB"), heat(per_pixel)
    w, ht = a.size
    sheet = Image.new("RGB", (w * 3, ht))
    for i, im in enumerate((a, b, h)):
        sheet.paste(im, (i * w, 0))
    # half size: three 1600-wide pictures side by side are big enough at 2400
    sheet = sheet.resize((w * 3 // 2, ht // 2), Image.LANCZOS)
    sheet.save(out_path, optimize=True)


def main(argv):
    args = [a for a in argv[1:] if not a.startswith("--")]
    opts = {k: v for k, v in zip(argv[1:], argv[2:]) if k.startswith("--")}
    if len(args) < 2:
        print(__doc__)
        return 2
    a_root, b_root = args[0].rstrip("/\\"), args[1].rstrip("/\\")
    a_pics, b_pics = pictures(a_root), pictures(b_root)
    both = sorted(a_pics.keys() & b_pics.keys())
    noise = {}
    if "--noise" in opts:
        with open(opts["--noise"], encoding="utf-8") as f:
            noise = json.load(f)

    results = {}
    for rel in both:
        stats, per_pixel = compare(a_pics[rel], b_pics[rel])
        results[rel] = (stats, per_pixel)

    if "--write-noise" in opts:
        floor = {rel: s["changed"] for rel, (s, _) in results.items() if s}
        with open(opts["--write-noise"], "w", encoding="utf-8") as f:
            json.dump(floor, f, indent=1, sort_keys=True)
        worst = sorted(floor.items(), key=lambda kv: -kv[1])[:10]
        print(f"noise floor of {len(floor)} shots written to {opts['--write-noise']}; noisiest:")
        for rel, c in worst:
            print(f"  {c * 100:6.2f} %  {rel}")
        return 0

    out = opts.get("--out", os.path.join(b_root, "diff-" + os.path.basename(a_root)))
    os.makedirs(out, exist_ok=True)
    cls = classes()
    rows, over = [], 0
    for rel in both:
        stats, per_pixel = results[rel]
        view = os.path.splitext(os.path.basename(rel))[0]
        group = "models" if "/models/" in rel else cls.get(view, "other")
        if stats is None:
            rows.append((group, rel, None, None, True, None))
            over += 1
            continue
        limit = noise[rel] * 2 + 0.002 if rel in noise else DEFAULT
        bad = stats["changed"] > limit
        sheet = None
        if bad or (group != "models" and stats["changed"] > 0):
            sheet = rel.replace("/", "__").replace(".png", ".diff.png")
            composite(a_pics[rel], b_pics[rel], per_pixel, os.path.join(out, sheet))
        over += bad
        rows.append((group, rel, stats, limit, bad, sheet))

    only_a = sorted(a_pics.keys() - b_pics.keys())
    only_b = sorted(b_pics.keys() - a_pics.keys())
    order = {"near": 0, "mid": 1, "far": 2, "edge": 3, "night": 4, "other": 5, "models": 6}
    rows.sort(key=lambda r: (order.get(r[0], 5), -(r[2]["changed"] if r[2] else 1), r[1]))

    # summary.md, for a PR
    md = [f"# Reference screenshots: `{os.path.basename(a_root)}` vs `{os.path.basename(b_root)}`", "",
          f"{len(both)} shots compared, **{over} over threshold**"
          + (f", {len(only_a)} only in A, {len(only_b)} only in B" if only_a or only_b else ""), "",
          "| class | shots | over | worst changed | worst shot |", "|---|---|---|---|---|"]
    for g in sorted({r[0] for r in rows}, key=lambda g: order.get(g, 5)):
        rs = [r for r in rows if r[0] == g]
        worst = max(rs, key=lambda r: r[2]["changed"] if r[2] else 1)
        wc = f"{worst[2]['changed'] * 100:.2f} %" if worst[2] else "size differs"
        md.append(f"| {g} | {len(rs)} | {sum(r[4] for r in rs)} | {wc} | `{worst[1]}` |")
    with open(os.path.join(out, "summary.md"), "w", encoding="utf-8") as f:
        f.write("\n".join(md) + "\n")

    # report.html, to look at
    h = ["<!doctype html><meta charset=utf-8><title>refshot diff</title>",
         "<style>body{font:14px system-ui;margin:16px;background:#111;color:#ddd}"
         "table{border-collapse:collapse}td,th{padding:3px 8px;border-bottom:1px solid #333}"
         ".bad{color:#f66;font-weight:bold}img{max-width:100%;margin:6px 0 18px}a{color:#8cf}</style>",
         f"<h1>{html.escape(os.path.basename(a_root))} vs {html.escape(os.path.basename(b_root))}</h1>",
         f"<p>{len(both)} shots, <b>{over} over threshold</b>. Each sheet: A | B | difference (yellow small, red large).</p>"]
    for g in sorted({r[0] for r in rows}, key=lambda g: order.get(g, 5)):
        h.append(f"<h2>{html.escape(g)}</h2><table><tr><th>shot</th><th>changed</th><th>limit</th><th>mean</th><th>max</th></tr>")
        for group, rel, stats, limit, bad, sheet in rows:
            if group != g:
                continue
            name = f"<a href='#{html.escape(rel)}'>{html.escape(rel)}</a>" if sheet else html.escape(rel)
            if stats is None:
                h.append(f"<tr class=bad><td>{name}</td><td colspan=4>picture sizes differ</td></tr>")
                continue
            h.append(f"<tr class={'bad' if bad else ''}><td>{name}</td><td>{stats['changed'] * 100:.3f} %</td>"
                     f"<td>{limit * 100:.2f} %</td><td>{stats['mean']:.2f}</td><td>{stats['max']}</td></tr>")
        h.append("</table>")
        for group, rel, stats, limit, bad, sheet in rows:
            if group == g and sheet:
                h.append(f"<h3 id='{html.escape(rel)}'>{html.escape(rel)}</h3><img src='{html.escape(sheet)}'>")
    if only_a or only_b:
        h.append("<h2>missing</h2><ul>" + "".join(f"<li>only in A: {html.escape(r)}" for r in only_a)
                 + "".join(f"<li>only in B: {html.escape(r)}" for r in only_b) + "</ul>")
    with open(os.path.join(out, "report.html"), "w", encoding="utf-8") as f:
        f.write("\n".join(h))

    print("\n".join(md))
    print(f"\nreport: {os.path.join(out, 'report.html')}")
    return 1 if over else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))

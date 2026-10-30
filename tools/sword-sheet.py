#!/usr/bin/env python3
"""Contact sheet of the sword's body poses (#1371): one row per move (set), one column per frame.

    <godot> --path . -- --models,test_output/sword_poses_cartoon --modelsonly "Sword poses" --modelsyaw 90 --style cartoon
    python tools/sword-sheet.py test_output/sword_poses_cartoon test_output/sword_poses_cartoon_sheet.png
"""
import re
import sys
from pathlib import Path
from PIL import Image, ImageDraw

src, out = Path(sys.argv[1]), sys.argv[2]
rows = {}
for f in sorted(src.glob("*.png")):
    m = re.search(r"sword_poses-(.+)_f(\d)\.png$", f.name)
    if m:
        rows.setdefault(m.group(1), {})[int(m.group(2))] = f
names = list(rows)
first = Image.open(next(iter(rows[names[0]].values())))
w, h = first.size
# the figure sits in the middle of the viewer's frame: keep a central crop
cw, ch = int(w * 0.34), int(h * 0.86)
x0, y0 = (w - cw) // 2, int(h * 0.02)
cols = max(len(r) for r in rows.values())
sheet = Image.new("RGB", (cols * cw, len(names) * ch), (30, 30, 36))
d = ImageDraw.Draw(sheet)
for r, name in enumerate(names):
    for c, f in sorted(rows[name].items()):
        sheet.paste(Image.open(f).convert("RGB").crop((x0, y0, x0 + cw, y0 + ch)), (c * cw, r * ch))
    d.text((6, r * ch + 4), name, fill=(255, 230, 120))
sheet.save(out)
print(out, sheet.size)

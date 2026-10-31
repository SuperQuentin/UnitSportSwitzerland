#!/usr/bin/env python3
"""Frames of a clip as one sheet, to look at a recording without playing it (#1371).

    python tools/clip-frames.py test_output/clips/<name>.mp4 <out.png> [fps] [start_s] [seconds] [columns] [width]
"""
import subprocess
import sys
import tempfile
from pathlib import Path
from PIL import Image, ImageDraw

mp4, out = sys.argv[1], sys.argv[2]
fps = float(sys.argv[3]) if len(sys.argv) > 3 else 4
start = float(sys.argv[4]) if len(sys.argv) > 4 else 0
secs = float(sys.argv[5]) if len(sys.argv) > 5 else 60
cols = int(sys.argv[6]) if len(sys.argv) > 6 else 6
width = int(sys.argv[7]) if len(sys.argv) > 7 else 320
with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as tmp:
    subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-ss", str(start), "-t", str(secs), "-i", mp4,
                    "-vf", f"fps={fps},scale={width}:-2", f"{tmp}/f%04d.png"], check=True)
    files = sorted(Path(tmp).glob("f*.png"))
    first = Image.open(files[0])
    w, h = first.size
    rows = (len(files) + cols - 1) // cols
    sheet = Image.new("RGB", (cols * w, rows * h))
    d = ImageDraw.Draw(sheet)
    for i, f in enumerate(files):
        sheet.paste(Image.open(f).convert("RGB"), ((i % cols) * w, (i // cols) * h))
        d.text(((i % cols) * w + 4, (i // cols) * h + 3), f"{start + i / fps:.2f}s", fill=(255, 255, 0))
    sheet.save(out)
    print(out, sheet.size, len(files), "frames")

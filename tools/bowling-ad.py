#!/usr/bin/env python3
"""A VHS-style ad for BowlinGland (#1336): the lanes, a bowler's throw, the pins and the pinsetter, the ball return,
a strike's replay on the lane's screen, the bar, the pool tables and the arcade, to "Midnight Drive".

    python tools/bowling-ad.py film      film the takes (one launch, --enter bowlingland, Cartoon, 1920x1080, 30 fps)
    python tools/bowling-ad.py sheets    contact sheets of the takes, to choose the seconds of each (CUTS below)
    python tools/bowling-ad.py cut       the ad: the chosen seconds, VHS look, captions, music
    PY=<a python with Pillow> for cut (the captions are drawn with the game's Saira faces).

Positions are BowlinGland's plan (2510_1140_26) as planned on Oct 28 2026: pair 25 (its decoration bowler on lane 0,
the player's game on lane 1) runs along -X, its approach starting at x 25.6, its pins at x 3.6, its return at x 23.5-25.5,
z -16.25. Takes go to test_output/bowlingad/takes/, the ad to test_output/bowlingad/bowlingland-vhs.mp4.
Music: "Midnight Drive" by congusbongus (CC0, assets/ASSETS.md), rendered with --trackerwav.
"""
import math
import os
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "test_output" / "bowlingad"
TAKES = OUT / "takes"
CLIPS = ROOT / "test_output" / "clips"
GODOT = os.environ.get("GODOT", shutil.which("godot") or "godot")
FFMPEG = os.environ.get("FFMPEG", shutil.which("ffmpeg") or "ffmpeg")
FPS = 30
SIZE = (1920, 1080)


def aim(eye, at):
    dx, dy, dz = at[0] - eye[0], at[1] - eye[1], at[2] - eye[2]
    return math.degrees(math.atan2(dy, math.hypot(dx, dz))), math.degrees(math.atan2(-dx, -dz))


def pose(eye, at):
    pitch, yaw = aim(eye, at)
    return f"{eye[0]:.2f},l{eye[1]:.2f},{eye[2]:.2f},{pitch:.1f},{yaw:.1f}"


def still(eye, at, seconds, name="~"):
    return f"{pose(eye, at)},{seconds},{name if name.startswith('~') else (OUT / (name + '.png')).as_posix()}"


def glide(eye, at, seconds, ease="~ease"):
    return f"{pose(eye, at)},{seconds},{ease}"


# (name, the lines placing the camera, the lines while filming, seconds filmed)
TAKES_LIST = [
    ("wide", [still((31.0, 2.6, -8.0), (5.0, 1.0, -12.0), 1, "p_wide")],
     [glide((31.0, 2.6, -8.0), (5.0, 1.0, -12.0), 0.5), glide((28.5, 2.1, -1.0), (5.0, 0.7, -3.0), 9)], 9.5),
    ("approach", [still((19.0, 1.7, -15.2), (25.0, 1.0, -17.4), 1, "p_approach")],
     [still((19.0, 1.7, -15.2), (25.0, 1.0, -17.4), 32, "e_approach")], 32),
    ("pins", [still((5.6, 0.75, -17.25), (3.4, 0.35, -17.09), 1, "p_pins")],
     [still((5.6, 0.75, -17.25), (3.4, 0.35, -17.09), 40, "e_pins")], 40),
    ("pinsetter", [still((4.9, 1.45, -16.6), (3.2, 0.7, -17.09), 1, "p_pinsetter")],
     [still((4.9, 1.45, -16.6), (3.2, 0.7, -17.09), 34, "e_pinsetter")], 34),
    ("return", [still((24.3, 1.45, -14.5), (24.6, 0.8, -16.25), 1, "p_return")],
     [still((24.3, 1.45, -14.5), (24.6, 0.8, -16.25), 34, "e_return")], 34),
    # your own game on lane 1: a house ball off the return, a strike, its replay on the lane's screen
    ("screen", [still((25.2, 2.55, -15.41), (23.85, 2.76, -15.41), 1, "p_screen"), "/bowl signin", "/bowl ball -1"],
     ["/bowl roll 0.05 -0.01 8.5 30 0.25", still((25.2, 2.55, -15.41), (23.85, 2.76, -15.41), 16, "e_screen")], 16),
    ("concourse", [still((36.5, 2.1, -19.6), (36.5, 1.0, 0.0), 1, "p_concourse")],
     [glide((36.5, 2.1, -19.6), (36.5, 1.0, 0.0), 0.5), glide((36.0, 1.9, -11.0), (33.0, 1.0, 2.0), 9)], 9.5),
]


def film():
    TAKES.mkdir(parents=True, exist_ok=True)
    lines = []
    for name, setup, moves, seconds in TAKES_LIST:
        lines += setup
        lines.append(f"/clip start {name} {seconds + 0.6:.1f} 0")
        lines += moves
    lines.append("quit")
    queue = OUT / "queue.txt"
    queue.write_text("\n".join(lines) + "\n", encoding="utf-8")
    args = [GODOT, "--screen", "1", "--resolution", f"{SIZE[0]}x{SIZE[1]}", "--fixed-fps", str(FPS), "--path", str(ROOT), "--",
            "--title", "BowlinGland ad", "--nocapture", "--enter", "bowlingland", "--time", "20", "--patronhour", "20",
            "--detail", "high", "--style", "cartoon", "--nohud", "--clip-hq", "--shot-queue", queue.as_posix()]
    print("[ad] filming", flush=True)
    with open(OUT / "film.log", "w", encoding="utf-8") as f:
        subprocess.run(args, stdout=f, stderr=subprocess.STDOUT, cwd=ROOT)
    for name, *_ in TAKES_LIST:
        mp4 = CLIPS / f"{name}.mp4"
        if mp4.exists():
            shutil.move(str(mp4), TAKES / f"{name}.mp4")
            (CLIPS / f"{name}.gif").unlink(missing_ok=True)
            print(f"[ad]   {name}: filmed", flush=True)
        else:
            print(f"[ad]   {name}: NOT FILMED (see film.log)", flush=True)


def sheets():
    for name, *_ in TAKES_LIST:
        src = TAKES / f"{name}.mp4"
        if not src.exists():
            continue
        subprocess.run([FFMPEG, "-hide_banner", "-loglevel", "error", "-y", "-i", str(src),
                        "-vf", "fps=2,scale=320:-1,drawbox=x=0:y=0:w=60:h=18:color=black@0.6:t=fill,tile=8x10",
                        "-frames:v", "1", str(OUT / f"sheet_{name}.png")], check=True)
        print(f"[ad] sheet_{name}.png (2 frames a second, left to right)")


# ---- the cut: (take, start s, length s, caption) in order; the seconds chosen from the sheets ----------------------
CUTS = [
    ("wide", 0.5, 4.5, "COSMIC BOWLING!"),
    ("approach", 12.5, 5.0, "BOWL LIKE A PRO"),
    ("pins", 5.0, 4.0, "PINS FLY!"),
    ("pinsetter", 23.5, 6.5, "REAL PINSETTERS"),
    ("return", 4.5, 5.0, "HOUSE BALLS ON THE RETURN"),
    ("screen", 7.0, 5.8, "INSTANT REPLAY!"),
    ("concourse", 6.5, 3.5, "POOL \u2022 BAR \u2022 ARCADE"),
]
MUSIC_FROM = 20.0


def font(size):
    from PIL import ImageFont
    return ImageFont.truetype(str(ROOT / "assets" / "fonts" / "SairaCondensed-ExtraBold.ttf"), size)


def caption_png(text, path, size=96, colour=(255, 230, 90)):
    """A big italic-ish 80s caption: magenta shadow, yellow face, cyan edge, on transparent."""
    from PIL import Image, ImageDraw
    img = Image.new("RGBA", SIZE, (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    f = font(size)
    w = d.textlength(text, font=f)
    x, y = (SIZE[0] - w) / 2, SIZE[1] * 0.72
    for dx, dy, c in [(8, 8, (255, 40, 180, 255)), (-3, -3, (40, 230, 255, 255)), (0, 0, colour + (255,))]:
        d.text((x + dx, y + dy), text, font=f, fill=c)
    img.save(path)


def osd_png(path):
    """The camcorder's on-screen display: PLAY at the top left, a 1987 date at the bottom right."""
    from PIL import Image, ImageDraw
    img = Image.new("RGBA", SIZE, (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    f = font(54)
    d.text((70, 50), "PLAY", font=f, fill=(245, 245, 245, 235))
    # the triangle drawn: the face has no glyph for it
    w = d.textlength("PLAY ", font=f)
    d.polygon([(70 + w, 66), (70 + w, 104), (70 + w + 34, 85)], fill=(245, 245, 245, 235))
    d.text((SIZE[0] - 520, SIZE[1] - 120), "OCT 28 1987  21:07", font=f, fill=(245, 245, 245, 235))
    img.save(path)


def card_png(title, sub, path):
    from PIL import Image, ImageDraw
    img = Image.new("RGB", SIZE, (14, 6, 34))
    d = ImageDraw.Draw(img)
    # a sunset grid, as the decade drew it
    for i in range(0, 14):
        y = SIZE[1] * 0.62 + i * i * 2.6
        d.line([(0, y), (SIZE[0], y)], fill=(255, 40, 180), width=3)
    for i in range(-20, 21):
        d.line([(SIZE[0] / 2 + i * 40, SIZE[1] * 0.62), (SIZE[0] / 2 + i * 240, SIZE[1])], fill=(255, 40, 180), width=3)
    for k, c in enumerate([(255, 220, 60), (255, 140, 60), (255, 60, 120)]):
        d.pieslice([SIZE[0] / 2 - 260 + k * 0, SIZE[1] * 0.62 - 260, SIZE[0] / 2 + 260, SIZE[1] * 0.62 + 260], 180, 360, fill=c) if k == 0 else None
    for y in range(int(SIZE[1] * 0.62) - 250, int(SIZE[1] * 0.62), 26):
        d.rectangle([0, y, SIZE[0], y + 8], fill=(14, 6, 34))
    f = font(190)
    w = d.textlength(title, font=f)
    for dx, dy, c in [(10, 10, (255, 40, 180)), (-4, -4, (40, 230, 255)), (0, 0, (255, 240, 120))]:
        d.text(((SIZE[0] - w) / 2 + dx, 60 + dy), title, font=f, fill=c)
    f2 = font(70)
    w2 = d.textlength(sub, font=f2)
    # over the sun, clear of its stripes
    d.text(((SIZE[0] - w2) / 2 + 4, 288), sub, font=f2, fill=(255, 40, 180))
    d.text(((SIZE[0] - w2) / 2, 284), sub, font=f2, fill=(40, 230, 255))
    img.save(path)


VHS = ("scale=720:404:flags=bilinear,"                       # the tape's resolution
       "gblur=sigma=0.7,"                                     # soft
       "rgbashift=rh=-5:bh=5:rv=1:bv=-1,"                     # the chroma off its luma
       "eq=saturation=1.45:contrast=1.08:brightness=0.02:gamma_r=1.05:gamma_b=0.95,"
       "noise=alls=14:allf=t+u,"                              # the grain
       "scale=1920:1080:flags=neighbor,"
       "geq=lum='lum(X,Y)*(0.82+0.18*mod(Y,4)/3)':cb='cb(X,Y)':cr='cr(X,Y)',"  # scanlines
       "vignette=PI/4.5")


def cut():
    if not CUTS:
        sys.exit("[ad] choose the seconds first: CUTS")
    work = OUT / "work"
    work.mkdir(parents=True, exist_ok=True)
    osd = work / "osd.png"
    osd_png(osd)
    card_png("BOWLINGLAND", "COSMIC BOWLING • GLAND", work / "title.png")
    card_png("BOWLINGLAND", "STRIKE TONIGHT!", work / "end.png")
    parts = []
    # the title card, 3 s, through the same tape
    title = work / "p00.mp4"
    subprocess.run([FFMPEG, "-hide_banner", "-loglevel", "error", "-y", "-loop", "1", "-t", "3", "-i", str(work / "title.png"),
                    "-vf", VHS + ",fps=30,format=yuv420p", "-c:v", "libx264", "-crf", "16", str(title)], check=True)
    parts.append(title)
    for i, (take, start, length, caption) in enumerate(CUTS, 1):
        part = work / f"p{i:02d}.mp4"
        cap = work / f"c{i:02d}.png"
        inputs = ["-ss", str(start), "-t", str(length), "-i", str(TAKES / f"{take}.mp4"), "-i", str(osd)]
        chain = f"[0:v]fps=30,{VHS}[v];[v][1:v]overlay[o]"
        if caption:
            caption_png(caption, cap)
            inputs += ["-i", str(cap)]
            # the caption slides in from the left over its first 0.3 s
            chain += f";[o][2:v]overlay=x='min(0,-W+W*t/0.3)':y=0[o2]"
            out = "[o2]"
        else:
            out = "[o]"
        subprocess.run([FFMPEG, "-hide_banner", "-loglevel", "error", "-y", *inputs, "-filter_complex", chain, "-map", out,
                        "-an", "-c:v", "libx264", "-crf", "16", "-pix_fmt", "yuv420p", str(part)], check=True)
        parts.append(part)
    end = work / f"p{len(CUTS) + 1:02d}.mp4"
    subprocess.run([FFMPEG, "-hide_banner", "-loglevel", "error", "-y", "-loop", "1", "-t", "3.5", "-i", str(work / "end.png"),
                    "-vf", VHS + ",fps=30,format=yuv420p", "-c:v", "libx264", "-crf", "16", str(end)], check=True)
    parts.append(end)
    lst = work / "parts.txt"
    lst.write_text("".join(f"file '{p.as_posix()}'\n" for p in parts))
    joined = work / "joined.mp4"
    subprocess.run([FFMPEG, "-hide_banner", "-loglevel", "error", "-y", "-f", "concat", "-safe", "0", "-i", str(lst),
                    "-c", "copy", str(joined)], check=True)
    total = 3 + sum(c[2] for c in CUTS) + 3.5
    music = ROOT / "test_output" / "tracker" / "midnight_drive.wav"
    film = OUT / "bowlingland-vhs.mp4"
    # the music from its full beat, a tape's wow on it, faded in and out
    subprocess.run([FFMPEG, "-hide_banner", "-loglevel", "error", "-y", "-i", str(joined), "-ss", str(MUSIC_FROM), "-i", str(music),
                    "-filter_complex",
                    f"[1:a]atrim=0:{total},asetpts=N/SR/TB,vibrato=f=0.6:d=0.06,highpass=f=60,lowpass=f=9000,"
                    f"afade=t=in:st=0:d=0.6,afade=t=out:st={total - 2.0}:d=2.0,volume=1.6[a]",
                    "-map", "0:v", "-map", "[a]", "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-shortest", str(film)], check=True)
    print(f"[ad] {film} ({total:.1f} s)")


if __name__ == "__main__":
    what = sys.argv[1] if len(sys.argv) > 1 else ""
    {"film": film, "sheets": sheets, "cut": cut}.get(what, lambda: sys.exit(__doc__))()

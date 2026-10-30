#!/usr/bin/env python3
"""The pinball showcase film (#1325): the four tables in their venues, a robot at the flippers.

    python tools/pinball-showcase.py stills          one launch per venue: a still of every take's first pose (and /pinball's list)
    python tools/pinball-showcase.py film [take ...] film the takes (all, or those named)
    python tools/pinball-showcase.py cut             cut the filmed takes with the captions
    python tools/pinball-showcase.py all             film and cut

Same way as tools/cinema-showcase.py (docs/notes/trailer/cinema-showcase.md): an "i" line through the venue's
door, then "l" lines and glides in the venue's plan frame, "/clip start" filming each take at the window's size
(--clip-hq, --fixed-fps 30). "/pinball play <n>", "start" and "robot" put a robot at a machine's flippers; the
shot camera stays the queue's. Every launch runs on a user:// of its own (APPDATA under OUT), seeded with high
scores so an idle machine shows them in attract mode, so the player's own scores are never touched.
Takes go to OUT/takes, the film to OUT/pinball.mp4.

Env: GODOT (the console exe), CHUNKS (terrain; default D:/UnitSportSwitzerland/terrain_chunks), FFMPEG, OUT
(default test_output/pinball-showcase). Machines are the venues' plans of Oct 29 2026 as generated after #1337, with #1350's tables: a
planner change that moves a piece moves the camera off it.
"""
import json
import math
import os
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT = Path(os.environ.get("OUT", ROOT / "test_output" / "pinball-showcase"))
TAKES = OUT / "takes"
CLIPS = ROOT / "test_output" / "clips"
GODOT = os.environ.get("GODOT", r"C:\ProgramData\chocolatey\lib\godot-mono\tools\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe")
CHUNKS = os.environ.get("CHUNKS", "D:/UnitSportSwitzerland/terrain_chunks")
FPS = 30
SIZE = "1920x1080"

TABLES = ["Clockwork", "Space Mission", "Haunted Castle", "Alpine Ski Race"]


def ffmpeg():
    if os.environ.get("FFMPEG"):
        return os.environ["FFMPEG"]
    found = shutil.which("ffmpeg")
    if found:
        return found
    winget = Path(os.environ.get("LOCALAPPDATA", "")) / "Microsoft" / "WinGet" / "Links" / "ffmpeg.exe"
    if winget.exists():
        return str(winget)
    krita = Path(r"C:\Program Files\Krita (x64)\bin\ffmpeg.exe")
    return str(krita) if krita.exists() else "ffmpeg"


# ---- the camera ---------------------------------------------------------------------------------------

def aim(eye, at):
    """Pitch and yaw (degrees) from eye toward at: Godot's camera looks down -Z, yaw about Y, pitch up positive."""
    dx, dy, dz = at[0] - eye[0], at[1] - eye[1], at[2] - eye[2]
    return math.degrees(math.atan2(dy, math.hypot(dx, dz))), math.degrees(math.atan2(-dx, -dz))


def pose(eye, at, height="l"):
    pitch, yaw = aim(eye, at)
    return f"{eye[0]:.2f},{height}{eye[1]:.2f},{eye[2]:.2f},{pitch:.1f},{yaw:.1f}"


def still(eye, at, seconds, name):
    return f"{pose(eye, at)},{seconds},{OUT.as_posix()}/{name}.png"


def glide(eye, at, seconds, ease="~ease"):
    return f"{pose(eye, at)},{seconds},{ease}"


class Machine:
    """A pinball piece in its venue's plan: its centre and turns (the player stands on its +Z, turned)."""

    def __init__(self, index, x, z, turns, table):
        self.index, self.x, self.z, self.table = index, x, z, table
        a = turns * math.pi / 2
        self.dx, self.dz = math.sin(a), math.cos(a)   # toward the player
        self.sx, self.sz = self.dz, -self.dx          # the player's right... or left: only used symmetrically

    def at(self, out, h, side=0.0):
        """A point `out` metres toward the player from the centre, `side` across, `h` over the floor."""
        return (self.x + self.dx * out + self.sx * side, h, self.z + self.dz * out + self.sz * side)

    # the poses every table's takes share
    def eye(self):
        return self.at(1.09, 1.5)

    def field(self):
        return self.at(-0.15, 0.95)

    def backbox(self):
        return self.at(-0.62, 1.62)


class Take:
    def __init__(self, name, caption, setup, moves, before=(), lead=0.6):
        self.name, self.caption, self.setup, self.moves, self.before, self.lead = name, caption, setup, moves, before, lead

    @property
    def seconds(self):
        return sum(float(m.split(",")[5]) for m in self.moves if not m.startswith("/"))

    def lines(self):
        out = list(self.setup)
        out.append(f"/clip start {self.name} {self.seconds + self.lead:.1f} 0")
        out += list(self.before)
        out += self.moves
        return out


def table_takes(m, tag, caption):
    """A machine's takes: walking up to it, the robot playing from the player's eye, straight down, the backbox."""
    start = [f"/pinball play {m.index}", "/pinball start"] + ["/pinball robot"]
    return [
        Take(f"{tag}-walkup", caption,
             [still(m.at(3.4, 1.75, 0.6), m.backbox(), 1, f"{tag}-walkup")] + start +
             # the robot plunges and the first ball is in play before the take
             [still(m.at(3.4, 1.75, 0.6), m.backbox(), 4, f"{tag}-walkup2")],
             [glide(m.at(1.5, 1.6, 0.15), m.at(-0.3, 1.25), 4, "~out")]),
        Take(f"{tag}-play", "",
             [still(m.eye(), m.field(), 0.3, f"{tag}-play")],
             [glide(m.at(1.0, 1.45), m.at(-0.2, 0.92), 8, "~lin")]),
        Take(f"{tag}-top", "",
             [still(m.at(-0.2, 2.05), m.at(-0.25, 0.8), 0.3, f"{tag}-top")],
             [glide(m.at(-0.2, 1.8), m.at(-0.25, 0.8), 6, "~ease")]),
        Take(f"{tag}-backbox", "",
             [still(m.at(0.9, 1.7, -0.35), m.backbox(), 0.3, f"{tag}-backbox")],
             [glide(m.at(0.75, 1.68, 0.25), m.backbox(), 4, "~ease")]),
    ]


def attract_take(m, tag, caption):
    """An idle machine's backbox cycling its high scores."""
    return Take(tag, caption,
                [still(m.at(1.3, 1.7, 0.3), m.backbox(), 0.3, tag)],
                [glide(m.at(0.85, 1.68, -0.2), m.backbox(), 7, "~lin")])


# ---- the venues ---------------------------------------------------------------------------------------
# Each venue's origin is its tile's corner, so a plan's door (tile frame: x east, z = 1000 m less north) is a world
# x, z - 1000. The "i" line stands 2.7 m out of the door, facing it.

def door_line(key, door, out, name):
    x, z = door[0] + out[0] * 2.7, door[1] - 1000 + out[1] * 2.7
    yaw = math.degrees(math.atan2(out[0], out[1]))
    return f"{x:.2f},i1.6,{z:.2f},-5,{yaw:.1f},3,{OUT.as_posix()}/{name}.png,{key}"


# Geneva, a bowling centre: Space Mission and Clockwork side by side
GENEVA = "2498_1115_190"
G_SPACE, G_CLOCK = Machine(15, 36.863, -45.662, 3, 1), Machine(16, 36.863, -46.568, 3, 0)
# near Gland, a bowling centre: Alpine Ski Race and Haunted Castle
VENOGE = "2505_1139_19"
V_ALPINE, V_CASTLE = Machine(7, 25.594, 23.288, 2, 3), Machine(8, 24.689, 23.288, 2, 2)
# BowlinGland: Alpine Ski Race by the toilets, Clockwork past the arcade cabinets (one launch each)
GLAND = "2510_1140_26"
B_CLOCK, B_ALPINE = Machine(7, 39.163, 16.016, 2, 0), Machine(8, 35.692, 16.016, 2, 3)


def geneva_takes():
    enter = [door_line(GENEVA, (720.1, 992.7), (0.02, 1.0), "g-in"), "/pinball", still(G_SPACE.at(3, 1.9, 0.5), G_SPACE.field(), 2, "g-list")]
    takes = table_takes(G_SPACE, "space", "Space Mission: 7-segment and alphanumeric displays")
    takes[0].setup = enter + takes[0].setup
    return takes + [attract_take(G_CLOCK, "clock-attract", "Clockwork: score reels, high scores to date")]


def venoge_takes():
    enter = [door_line(VENOGE, (234.6, 928.16), (0.86, 0.51), "v-in"), "/pinball", still(V_CASTLE.at(3, 1.9, 1.0), V_CASTLE.field(), 2, "v-list")]
    castle = table_takes(V_CASTLE, "castle", "Haunted Castle: a dot-matrix display")
    castle[0].setup = enter + castle[0].setup
    return castle + [attract_take(V_ALPINE, "alpine-attract", "Alpine Ski Race: an LCD results board")]


def gland_takes():
    enter = ["429.1,i1.6,-978.3,-5,43.4,3," + f"{OUT.as_posix()}/b-in.png", "/pinball", still(B_CLOCK.at(3, 1.9, -1.0), B_CLOCK.field(), 2, "b-list")]
    clock = table_takes(B_CLOCK, "clock", "Clockwork: a watchmaker's table, score reels and chimes")
    clock[0].setup = enter + clock[0].setup
    return clock


def gland_alpine_takes():
    """Alpine Ski Race at BowlinGland, a launch of its own: a second machine stepped up to after the first in one
    launch took the Start but never left attract."""
    enter = ["429.1,i1.6,-978.3,-5,43.4,3," + f"{OUT.as_posix()}/b2-in.png"]
    alpine = table_takes(B_ALPINE, "alpine", "Alpine Ski Race: two crossing ramps, slalom gates")
    alpine[0].setup = enter + alpine[0].setup
    return alpine


# the seeded high scores: (key, index, table) -> names and scores
SCORES = {
    (GENEVA, G_CLOCK.index, 0): [("NICOLAS", 2_418_330), ("QUENTIN", 1_960_110), ("ANNA", 1_204_870), ("LUCA", 866_420), ("MIA", 512_090)],
    (GENEVA, G_SPACE.index, 1): [("QUENTIN", 3_105_640), ("NICOLAS", 2_771_200), ("LEA", 1_420_000)],
    (VENOGE, V_ALPINE.index, 3): [("LEA", 1_877_410), ("NICOLAS", 1_502_300), ("NOAH", 990_150), ("ANNA", 645_880)],
    (VENOGE, V_CASTLE.index, 2): [("MIA", 2_250_660), ("QUENTIN", 1_314_980)],
    (GLAND, B_CLOCK.index, 0): [("ANNA", 1_120_440), ("LUCA", 760_320)],
    (GLAND, B_ALPINE.index, 3): [("NOAH", 1_699_010), ("LEA", 1_150_220), ("NICOLAS", 403_880)],
}

LAUNCHES = [
    # (name, origin, takes)
    ("geneva", "2498000,1115000", geneva_takes),
    ("venoge", "2505000,1139000", venoge_takes),
    ("gland", "2510000,1140000", gland_takes),
    ("gland-alpine", "2510000,1140000", gland_alpine_takes),
]


def userdata(name):
    """A user:// of the launch's own (APPDATA), the high scores seeded."""
    appdata = OUT / f"appdata_{name}"
    ud = appdata / "Godot" / "app_userdata" / "UnitSportSwitzerland"
    ud.mkdir(parents=True, exist_ok=True)
    rows = [{"Key": k, "Index": i, "Table": t, "Names": [n for n, _ in s], "Scores": [v for _, v in s]} for (k, i, t), s in SCORES.items()]
    (ud / "pinball_scores.json").write_text(json.dumps(rows, indent=2), encoding="utf-8")
    return appdata


def launch(name, origin, lines, hq):
    queue = OUT / f"{name}.txt"
    queue.write_text("\n".join(lines + ["quit"]) + "\n", encoding="utf-8")
    e, n = origin.split(",")
    args = [GODOT, "--resolution", SIZE, "--fixed-fps", str(FPS), "--path", str(ROOT), "--",
            "--title", f"pinball showcase {name}", "--nocapture", "--chunks", CHUNKS,
            "--origin", origin, "--originshift", "1000000", "--at", f"{e},{n}",
            "--detail", "high", "--style", "cartoon", "--time", "21", "--shot-queue", queue.as_posix()]
    if hq:
        # stills keep the HUD: /pinball's list of the venue's machines shows in the chat
        args += ["--nohud", "--clip-hq"]
    env = dict(os.environ)
    env["PATH"] = str(Path(ffmpeg()).parent) + os.pathsep + env.get("PATH", "")
    env["APPDATA"] = str(userdata(name))
    with open(OUT / f"{name}.log", "w", encoding="utf-8") as f:
        subprocess.run(args, stdout=f, stderr=subprocess.STDOUT, cwd=ROOT, env=env)


def stills():
    OUT.mkdir(parents=True, exist_ok=True)
    for name, origin, takes in LAUNCHES:
        print(f"[showcase] stills: {name}", flush=True)
        lines = []
        for t in takes():
            lines += t.setup
        launch(name, origin, lines, False)


def film(only):
    TAKES.mkdir(parents=True, exist_ok=True)
    for name, origin, takes in LAUNCHES:
        chosen = [t for t in takes() if not only or t.name in only]
        if not chosen:
            continue
        names = {t.name for t in chosen}
        lines = []
        for t in takes():
            lines += t.lines() if t.name in names else t.setup
        print(f"[showcase] {name}: {', '.join(t.name for t in chosen)}", flush=True)
        launch(name, origin, lines, True)
        for t in chosen:
            mp4 = CLIPS / f"{t.name}.mp4"
            if mp4.exists():
                shutil.move(str(mp4), TAKES / f"{t.name}.mp4")
                (CLIPS / f"{t.name}.gif").unlink(missing_ok=True)
                print(f"[showcase]   {t.name}: {t.seconds:.1f} s", flush=True)
            else:
                print(f"[showcase]   {t.name}: NOT FILMED (see {OUT / name}.log)", flush=True)


# ---- the cut ------------------------------------------------------------------------------------------

XFADE = 0.4
TRIM = 0.4
TITLE = 3.0
END = 4.0


def font(size, bold=True):
    from PIL import ImageFont
    face = ROOT / "assets" / "fonts" / ("SairaCondensed-ExtraBold.ttf" if bold else "SairaCondensed-SemiBold.ttf")
    return ImageFont.truetype(str(face), size)


def caption_png(text, path):
    from PIL import Image, ImageDraw, ImageFilter
    w, h = 1920, 1080
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    f = font(54)
    d = ImageDraw.Draw(img)
    box = d.textbbox((0, 0), text, font=f)
    tw, th = box[2] - box[0], box[3] - box[1]
    x, y = 96, h - 150 - th
    band = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    ImageDraw.Draw(band).rounded_rectangle((x - 28, y - 18, x + tw + 28, y + th + 30), 14, fill=(10, 8, 14, 150))
    img = Image.alpha_composite(img, band.filter(ImageFilter.GaussianBlur(2)))
    d = ImageDraw.Draw(img)
    d.rectangle((x - 28, y - 18, x - 20, y + th + 30), fill=(255, 196, 64, 255))
    d.text((x, y - box[1]), text, font=f, fill=(255, 255, 255, 255))
    img.save(path)


def card_png(title, sub, path):
    from PIL import Image, ImageDraw
    img = Image.new("RGB", (1920, 1080), (8, 6, 12))
    d = ImageDraw.Draw(img)
    for text, f, y, colour in ((title, font(128), 430, (255, 255, 255)), (sub, font(52, False), 600, (255, 196, 64))):
        box = d.textbbox((0, 0), text, font=f)
        d.text(((1920 - (box[2] - box[0])) / 2, y - box[1]), text, font=f, fill=colour)
    img.save(path)


# the film's order: each table in turn, its attract shot after it
ORDER = ["clock-walkup", "clock-play", "clock-top", "clock-backbox", "clock-attract",
         "space-walkup", "space-play", "space-top", "space-backbox",
         "castle-walkup", "castle-play", "castle-top", "castle-backbox",
         "alpine-walkup", "alpine-play", "alpine-top", "alpine-backbox", "alpine-attract"]


def cut():
    every = {t.name: t for _, _, ts in LAUNCHES for t in ts()}
    takes = [every[n] for n in ORDER if (TAKES / f"{n}.mp4").exists()]
    if not takes:
        sys.exit("no takes filmed: python tools/pinball-showcase.py film")
    work = OUT / "cut"
    work.mkdir(parents=True, exist_ok=True)
    card_png("PINBALL", "UnitSport Switzerland", work / "title.png")
    card_png("Four tables, one in every venue", "Flippers, ramps, multiball, high scores", work / "end.png")

    inputs, lengths = [], []
    inputs += ["-loop", "1", "-t", f"{TITLE}", "-framerate", str(FPS), "-i", str(work / "title.png")]
    lengths.append(TITLE)
    for t in takes:
        inputs += ["-ss", f"{TRIM}", "-t", f"{t.seconds - TRIM - 0.05:.2f}", "-i", str(TAKES / f"{t.name}.mp4")]
        lengths.append(t.seconds - TRIM - 0.05)
    inputs += ["-loop", "1", "-t", f"{END}", "-framerate", str(FPS), "-i", str(work / "end.png")]
    lengths.append(END)

    parts, starts, last = [], [], "[v0]"
    parts.append(f"[0:v]format=yuv420p,setsar=1,fps={FPS},settb=AVTB[v0]")
    for i in range(1, len(lengths)):
        parts.append(f"[{i}:v]scale=1920:1080,format=yuv420p,setsar=1,fps={FPS},settb=AVTB[s{i}]")
    at = lengths[0]
    for i in range(1, len(lengths)):
        offset = at - XFADE
        starts.append(offset)
        parts.append(f"{last}[s{i}]xfade=transition=fade:duration={XFADE}:offset={offset:.3f}[x{i}]")
        last = f"[x{i}]"
        at = offset + lengths[i]
    total = at

    n_in, cap_inputs, ncap = len(lengths), [], 0
    for k, t in enumerate(takes):
        if not t.caption:
            continue
        png = work / f"cap_{t.name}.png"
        caption_png(t.caption, png)
        start, dur = starts[k] + 0.4, 5.0
        cap_inputs += ["-loop", "1", "-t", f"{dur:.2f}", "-framerate", str(FPS), "-i", str(png)]
        idx = n_in + ncap
        parts.append(f"[{idx}:v]format=rgba,fade=t=in:st=0:d=0.4:alpha=1,fade=t=out:st={dur - 0.4:.2f}:d=0.4:alpha=1,"
                     f"setpts=PTS+{start:.3f}/TB[c{idx}]")
        parts.append(f"{last}[c{idx}]overlay=0:0:eof_action=pass[o{idx}]")
        last = f"[o{idx}]"
        ncap += 1

    out = OUT / "pinball.mp4"
    args = [ffmpeg(), "-hide_banner", "-loglevel", "error", "-y", *inputs, *cap_inputs,
            "-filter_complex", ";".join(parts), "-map", last, "-t", f"{total:.2f}",
            "-c:v", "libopenh264", "-b:v", "16M", "-movflags", "+faststart", str(out)]
    if b"libx264" in subprocess.run([ffmpeg(), "-hide_banner", "-encoders"], capture_output=True).stdout:
        args[args.index("libopenh264"):args.index("libopenh264") + 3] = ["libx264", "-crf", "18"]
    subprocess.run(args, check=True)
    print(f"[showcase] {out} ({total:.1f} s, {len(takes)} takes, {out.stat().st_size / 1e6:.0f} MB)")


if __name__ == "__main__":
    mode = sys.argv[1] if len(sys.argv) > 1 else "all"
    if mode == "stills":
        stills()
    if mode in ("film", "all"):
        film(set(sys.argv[2:]))
    if mode in ("cut", "all"):
        cut()

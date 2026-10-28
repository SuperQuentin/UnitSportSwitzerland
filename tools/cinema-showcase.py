#!/usr/bin/env python3
"""The cinema showcase film (#1306): the Cinemas du Grutli in Geneva, then the city's other venues at night.

    python tools/cinema-showcase.py film [take ...]   film the takes (all, or those named: lobby beam ...)
    python tools/cinema-showcase.py cut               cut the filmed takes with the captions and the music
    python tools/cinema-showcase.py all               both

Each location is one game launch: an "i" line steps through the building's door, then "l" lines and glides
move the camera in the building's own plan frame (docs/notes/core/commands.md), and "/clip start" films each
take in Cartoon at the window's size (--clip-hq, --fixed-fps 30: every frame, never dropped). "/cinema" sets a
hall's lights and film. Takes go to test_output/showcase/takes/, the film to test_output/showcase/cinema.mp4.
The soundtrack is the cinemas' own film music, crusaders.xm (src/Audio/Tracker, ASSETS.md), rendered with
--trackerwav. Captions are PNGs drawn here (Pillow, the game's Saira faces): the ffmpeg at hand may have no
text filter.

Env: GODOT (the console exe), CHUNKS (terrain; default D:/UnitSportSwitzerland/terrain_chunks), FFMPEG.
Positions are the plans of the interiors as planned on Oct 28 2026 (CinemaRules 14): a planner change that
moves a room moves the camera off it; re-read the plans (user://interiors/<tile>/<n>.json).
"""
import math
import os
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "test_output" / "showcase"
TAKES = OUT / "takes"
CLIPS = ROOT / "test_output" / "clips"
GODOT = os.environ.get("GODOT", r"C:\ProgramData\chocolatey\lib\godot-mono\tools\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe")
CHUNKS = os.environ.get("CHUNKS", "D:/UnitSportSwitzerland/terrain_chunks")
FPS = 30
SIZE = "1920x1080"


def ffmpeg():
    if os.environ.get("FFMPEG"):
        return os.environ["FFMPEG"]
    found = shutil.which("ffmpeg")
    if found:
        return found
    # winget's full build (https, libx264), else Krita's (neither, #1105)
    winget = Path(os.environ.get("LOCALAPPDATA", "")) / "Microsoft" / "WinGet" / "Links" / "ffmpeg.exe"
    if winget.exists():
        return str(winget)
    krita = Path(r"C:\Program Files\Krita (x64)\bin\ffmpeg.exe")
    return str(krita) if krita.exists() else "ffmpeg"


# ---- the camera ---------------------------------------------------------------------------------------

def aim(eye, at):
    """Pitch and yaw (degrees) from eye toward at: Godot's camera looks down -Z, yaw about Y, pitch up positive."""
    dx, dy, dz = at[0] - eye[0], at[1] - eye[1], at[2] - eye[2]
    yaw = math.degrees(math.atan2(-dx, -dz))
    pitch = math.degrees(math.atan2(dy, math.hypot(dx, dz)))
    return pitch, yaw


def pose(eye, at, height="l"):
    """x,<height><y>,z,pitch,yaw: a shot line's first five fields; height "l" inside, "g" above the ground."""
    pitch, yaw = aim(eye, at)
    return f"{eye[0]:.2f},{height}{eye[1]:.2f},{eye[2]:.2f},{pitch:.1f},{yaw:.1f}"


def still(eye, at, seconds, name, height="l"):
    return f"{pose(eye, at, height)},{seconds},{OUT.as_posix()}/{name}.png"


def glide(eye, at, seconds, ease="~ease", height="l"):
    return f"{pose(eye, at, height)},{seconds},{ease}"


class Take:
    """One filmed piece: the lines that place the camera (not filmed), then the clip over its glides."""

    def __init__(self, name, caption, setup, moves, before=(), lead=0.6):
        self.name, self.caption, self.setup, self.moves, self.before, self.lead = name, caption, setup, moves, before, lead

    @property
    def seconds(self):
        return sum(float(m.split(",")[5]) for m in self.moves if not m.startswith("/"))

    def lines(self):
        out = list(self.setup)
        # a little over the moves: the first frames are trimmed in the cut (the clip starts a frame late). The queue
        # holds this line until the take before is written, so what the take changes ("before") comes after it
        out.append(f"/clip start {self.name} {self.seconds + self.lead:.1f} 0")
        out += list(self.before)
        out += self.moves
        return out


# ---- the takes ----------------------------------------------------------------------------------------
# Geneva: the world origin on the Grutli's door (LV95 2499892.6, 1117461.7), so a world x, z is metres east
# and south of it. Plan frames: x, z across the building, y over the ground floor.

GRUTLI = "2499_1117_152"
CINEMA_DOOR = (2.7, "i1.6", -1.3, 0, 115.5)   # 2.7 m in front of the door, facing it
HALL2_PORT = (0.8, 4.5, 13.6)                 # the middle hall's porthole
HALL2_LENS = (0.8, 4.35, 14.0)

# open films (#1298: a hall plays a web link on the shared clock; /cinema play acts on the hall the camera is in)
BUNNY = "https://www.youtube.com/watch?v=aqz-KE-bpKQ"
SINTEL = "https://www.youtube.com/watch?v=eRsGyueVLvQ"
SINTEL_TRAILER = "https://download.blender.org/durian/trailer/sintel_trailer-480p.mp4"


def cinema_takes():
    enter = [f"{CINEMA_DOOR[0]},{CINEMA_DOOR[1]},{CINEMA_DOOR[2]},{CINEMA_DOOR[3]},{CINEMA_DOOR[4]},3,{OUT.as_posix()}/in.png,{GRUTLI}",
             # every hall as the film wants it: 1 dimmed mid-film, 2 lit and waiting, 3 dark mid-film
             "/cinema 1 lights dim film on", "/cinema 2 lights full film off", "/cinema 3 lights off film on",
             # hall 1 plays Sintel from now on; hall 2 waits, lit, for its film (the lights take)
             still((-13.1, 3.6, 3.5), (-13.1, 4.0, -7), 0.3, "h1"), f"/cinema play {SINTEL}",
             still((0.8, 3.6, 3.5), (0.8, 4.0, -7), 0.3, "h2"), "/cinema stop", "/cinema 2 lights full film off"]
    door = (0.0, 2.2, 0.0)
    return [
        Take("exterior", "Cinémas du Grütli, Geneva",
             # down the street the door faces
             [still((40.5, 6, -19.4), door, 3, "ext0", "g")],
             [glide((17.0, 1.8, -8.2), (0, 3.2, 0), 7, "~out", "g")]),
        Take("lobby", "A box office, a snack bar, the queue",
             enter + [still((5, 1.7, -10.4), (2, 1.2, -16), 1, "lobby0")],
             [glide((-15, 1.7, -10.4), (-18, 1.1, -16), 7, "~lin")]),
        Take("aisle", "Steps by the seats, a service door to the booth",
             [still((7.0, 2.2, 2.0), (7.0, 3.2, 13.5), 1, "aisle0")],
             [glide((7.0, 3.5, 10.4), (7.1, 3.0, 13.5), 5, "~ease")]),
        Take("lights", "The house lights go down, slowly",
             [still((0.8, 6.0, 11.8), (0.8, 3.6, -7), 6, "lights0")],
             [glide((0.8, 4.4, 4.0), (0.8, 3.4, -7), 9, "~ease")],
             # the lights go down, and the film comes on a few seconds later, as it does
             before=["/cinema 2 lights off film on", f"/cinema play {BUNNY}"]),
        Take("films", "Every hall shows its own film",
             # a hall decodes its film while the camera is in it: wait there until it runs
             [still((14.7, 3.6, 3.5), (14.7, 4.0, -7), 0.3, "films0"), f"/cinema play {SINTEL_TRAILER}",
              still((14.7, 3.6, 3.5), (14.7, 4.0, -7), 24, "films1")],
             [glide((14.7, 2.6, -1.0), (14.7, 4.2, -7), 5, "~ease")]),
        Take("films2", "",
             [still((-13.1, 3.6, 3.5), (-13.1, 4.0, -7), 12, "films2")],
             [glide((-13.1, 2.6, -1.0), (-13.1, 4.2, -7), 4, "~ease")]),
        Take("beam", "Light thrown from the booth",
             [still((5.0, 1.5, -6.0), HALL2_PORT, 1, "beam0")],
             [glide((3.4, 3.4, 4.0), HALL2_PORT, 8, "~ease")]),
        Take("booth", "Up in the booth: a projector and a panel for each hall",
             [still((3.4, 4.6, 16.0), (2.6, 3.9, 13.9), 1, "booth0")],
             [glide((1.9, 4.5, 15.9), (0.8, 4.3, 14.2), 5, "~ease"),
              glide((0.6, 4.55, 15.6), HALL2_PORT, 5, "~ease")]),
    ]


def city_takes():
    """Geneva's other venues, the same launch as the cinema (the same origin)."""
    return [
        Take("pizzeria", "Pizzerias",
             [f"-50.2,i1.6,27.5,-5,116.1,3,{OUT.as_posix()}/pz.png,2499_1117_99",
              still((6.0, 1.8, -4.3), (14, 1.0, 4), 1, "pz0")],
             [glide((11.0, 1.6, -3.5), (13, 1.1, 4.5), 5, "~ease")]),
        Take("pub", "Pubs",
             [f"274.2,i1.6,-106.3,-5,138.7,3,{OUT.as_posix()}/pub.png,2500_1117_84",
              still((-6.8, 1.8, -28.2), (-2.6, 1.0, -21), 1, "pub0")],
             [glide((-4.0, 1.6, -26.5), (-2.6, 1.0, -20.7), 4, "~ease")]),
        Take("billiard", "Billiard halls",
             [still((1590, 2.0, -262), (1564.3, 3.5, -244.3), 3, "bl0", "g")],
             [glide((1576, 1.7, -253), (1564.3, 3.0, -244.3), 4, "~out", "g")]),
        Take("billiard-in", "",
             [f"1566.6,i1.6,-245.8,-5,123.5,3,{OUT.as_posix()}/bl.png,2501_1117_180",
              still((6.0, 2.2, -42.0), (-7, 0.8, -30), 1, "bl1")],
             [glide((-1.0, 1.7, -40.0), (-11, 1.0, -28), 6, "~ease")]),
        Take("toilets", "And yes, the toilets",
             [still((6.0, 1.6, -30.0), (0, 1.8, -24.2), 1, "wc0")],
             [glide((3.0, 1.7, -28.0), (2, 2.0, -24.2), 3, "~ease"),
              # through the door: the men's, urinals with their screens
              glide((3.6, 1.6, -21.6), (1.2, 1.0, -23.8), 0.04, "~lin"),
              glide((2.9, 1.6, -21.9), (0.8, 1.0, -23.8), 3, "~ease")]),
    ]


# Gland: the origin of docs/notes/terrain/bowling-centres.md (2510000, 1140000)
def bowling_takes():
    return [
        Take("bowling", "Bowling alleys",
             [still((401.0, 2.0, -929.0), (388.9, 4.5, -944.6), 3, "bw0", "g")],
             [glide((396.0, 1.7, -936.0), (388.9, 4.0, -944.6), 4, "~out", "g")]),
        Take("lanes", "",
             ["394.4,i1.6,-938.8,-6,313.4,3," + f"{OUT.as_posix()}/bw.png",
              still((28.4, 2.3, -15.5), (-1.4, 0.6, -15.5), 1, "bw1")],
             [glide((28.4, 2.3, 10.0), (-1.4, 0.6, 10.0), 7, "~ease")]),
    ]


LAUNCHES = [
    # (name, origin, time of day, takes)
    ("geneva", "2499892.6,1117461.7", 21, lambda: cinema_takes() + city_takes()),
    ("gland", "2510000,1140000", 21, bowling_takes),
]


def film(only):
    TAKES.mkdir(parents=True, exist_ok=True)
    for name, origin, hour, takes in LAUNCHES:
        chosen = [t for t in takes() if not only or t.name in only]
        if not chosen:
            continue
        names = {t.name for t in chosen}
        lines = []
        for t in takes():
            # a take not asked for still places the camera (the door it goes in by), but films nothing
            lines += t.lines() if t.name in names else t.setup
        lines.append("quit")
        queue = OUT / f"{name}.txt"
        queue.write_text("\n".join(lines) + "\n", encoding="utf-8")
        log = OUT / f"{name}.log"
        e, n = origin.split(",")
        args = [GODOT, "--resolution", SIZE, "--fixed-fps", str(FPS), "--path", str(ROOT), "--",
                "--title", f"1306 showcase {name}", "--nocapture", "--chunks", CHUNKS,
                "--origin", origin, "--originshift", "1000000", "--at", f"{e},{n}",
                "--detail", "high", "--style", "cartoon", "--time", str(hour), "--nohud", "--clip-hq",
                "--shot-queue", queue.as_posix()]
        print(f"[showcase] {name}: {', '.join(t.name for t in chosen)}", flush=True)
        # the game films through the ffmpeg on its PATH (BundledTools.Resolve)
        env = dict(os.environ)
        env["PATH"] = str(Path(ffmpeg()).parent) + os.pathsep + env.get("PATH", "")
        with open(log, "w", encoding="utf-8") as f:
            subprocess.run(args, stdout=f, stderr=subprocess.STDOUT, cwd=ROOT, env=env)
        for t in chosen:
            mp4 = CLIPS / f"{t.name}.mp4"
            if mp4.exists():
                shutil.move(str(mp4), TAKES / f"{t.name}.mp4")
                (CLIPS / f"{t.name}.gif").unlink(missing_ok=True)
                print(f"[showcase]   {t.name}: {t.seconds:.1f} s", flush=True)
            else:
                print(f"[showcase]   {t.name}: NOT FILMED (see {log})", flush=True)


# ---- the cut ------------------------------------------------------------------------------------------

XFADE = 0.5
TRIM = 0.4   # the clip's first frames: the camera not yet moving, the perf overlay not yet hidden
TITLE = 3.0  # the title card's length
END = 4.0


def font(size, bold=True):
    from PIL import ImageFont
    face = ROOT / "assets" / "fonts" / ("SairaCondensed-ExtraBold.ttf" if bold else "SairaCondensed-SemiBold.ttf")
    return ImageFont.truetype(str(face), size)


def caption_png(text, path):
    """A lower-third caption: white condensed letters on a soft dark band, transparent elsewhere."""
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


def cut():
    takes = [t for _, _, _, ts in LAUNCHES for t in ts()]
    takes = [t for t in takes if (TAKES / f"{t.name}.mp4").exists()]
    if not takes:
        sys.exit("no takes filmed: python tools/cinema-showcase.py film")
    work = OUT / "cut"
    work.mkdir(parents=True, exist_ok=True)
    card_png("CINEMAS", "UnitSport Switzerland", work / "title.png")
    card_png("A night out in Switzerland", "Cinemas, billiards, pizzerias, pubs, bowling", work / "end.png")

    inputs, lengths, captions = [], [], []
    inputs += ["-loop", "1", "-t", f"{TITLE}", "-framerate", str(FPS), "-i", str(work / "title.png")]
    lengths.append(TITLE)
    for t in takes:
        # the moves end a frame or two before the clip: stop short of the next take's first pose
        inputs += ["-ss", f"{TRIM}", "-t", f"{t.seconds - TRIM - 0.05:.2f}", "-i", str(TAKES / f"{t.name}.mp4")]
        lengths.append(t.seconds - TRIM - 0.05)
    inputs += ["-loop", "1", "-t", f"{END}", "-framerate", str(FPS), "-i", str(work / "end.png")]
    lengths.append(END)

    # the chain of crossfades, and when each take starts in the film
    parts, starts, at, last = [], [], 0.0, "[v0]"
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

    # captions over their takes, faded in and out
    n_in = len(lengths)
    cap_inputs = []
    for k, t in enumerate(takes):
        if not t.caption:
            continue
        png = work / f"cap_{t.name}.png"
        caption_png(t.caption, png)
        start = starts[k] + 0.4
        dur = min(lengths[k + 1] - 0.8, 4.0)
        cap_inputs += ["-loop", "1", "-t", f"{dur:.2f}", "-framerate", str(FPS), "-i", str(png)]
        idx = n_in + len(captions)
        parts.append(f"[{idx}:v]format=rgba,fade=t=in:st=0:d=0.4:alpha=1,fade=t=out:st={dur - 0.4:.2f}:d=0.4:alpha=1,"
                     f"setpts=PTS+{start:.3f}/TB[c{idx}]")
        parts.append(f"{last}[c{idx}]overlay=0:0:eof_action=pass[o{idx}]")
        last = f"[o{idx}]"
        captions.append(t.name)

    music = ROOT / "test_output" / "tracker" / "crusaders.wav"
    if not music.exists():
        subprocess.run([GODOT, "--headless", "--path", str(ROOT), "--", "--trackerwav", "crusaders.xm"], cwd=ROOT, check=True)
    audio_idx = n_in + len(captions)
    parts.append(f"[{audio_idx}:a]aresample=48000,afade=t=in:d=1.5,afade=t=out:st={total - 3:.2f}:d=3,atrim=0:{total:.2f}[a]")

    out = OUT / "cinema.mp4"
    args = [ffmpeg(), "-hide_banner", "-loglevel", "error", "-y", *inputs, *cap_inputs, "-i", str(music),
            "-filter_complex", ";".join(parts), "-map", last, "-map", "[a]", "-t", f"{total:.2f}",
            "-c:v", "libopenh264", "-b:v", "16M", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", str(out)]
    if b"libx264" in subprocess.run([ffmpeg(), "-hide_banner", "-encoders"], capture_output=True).stdout:
        args[args.index("libopenh264"):args.index("libopenh264") + 3] = ["libx264", "-crf", "16"]
    subprocess.run(args, check=True)
    print(f"[showcase] {out} ({total:.1f} s, {len(takes)} takes, {out.stat().st_size / 1e6:.0f} MB)")


if __name__ == "__main__":
    mode = sys.argv[1] if len(sys.argv) > 1 else "all"
    if mode in ("film", "all"):
        film(set(sys.argv[2:]))
    if mode in ("cut", "all"):
        cut()

#!/usr/bin/env python3
"""How far two renders of the same shots drift apart (#924).

Compares two track dumps of the trailer director (`TRACKS=DIR tools/trailer.sh ...`, one
`shotNN.tracks.csv` per shot: every actor and staged object each frame, LV95 metres) and reports,
per shot and object: the first frame that differs, the largest and the mean position difference,
the largest yaw difference, how many frames disagree on pose, ride or beam, and a verdict:

    identical   every row the same to the millimetre and the hundredth of a degree
    sub-cm      positions within 1 cm
    small       within 5 cm
    visible     more than 5 cm somewhere (a viewer could see it in a close shot)
    diverged    more than 1 m somewhere (a different take)

Usage:
    tools/trailer-drift.py A B [--markdown] [--shots 4,26] [--objects actor,cow]
A and B are track dump directories (or two .tracks.csv files). Rows are matched by (frame, id):
an object only one side has is listed; the camera is compared like an actor (keys on actors move it).
Exit code 0 always; it reports, it does not judge.
"""
import csv
import math
import os
import sys
from collections import defaultdict


def load(path):
    """{id: {frame: row}} and {id: kind} of one dump file."""
    rows = defaultdict(dict)
    kinds = {}
    with open(path, newline="") as f:
        for r in csv.DictReader(f):
            frame = int(r["frame"])
            rows[r["id"]][frame] = r
            kinds[r["id"]] = r["kind"]
    return rows, kinds


def verdict(identical, max_d):
    if identical:
        return "identical"
    if max_d < 0.01:
        return "sub-cm"
    if max_d < 0.05:
        return "small"
    if max_d <= 1.0:
        return "visible"
    return "diverged"


def compare(a_path, b_path, kinds_wanted=None):
    """One result row per object of the shot."""
    a, ka = load(a_path)
    b, kb = load(b_path)
    out = []
    for oid in sorted(set(a) | set(b), key=lambda s: (s != "camera", s)):
        kind = ka.get(oid) or kb.get(oid)
        if kinds_wanted and kind not in kinds_wanted:
            continue
        if oid not in a or oid not in b:
            out.append(dict(id=oid, kind=kind, frames=len(a.get(oid, b.get(oid))), first=None, max=None, mean=None,
                            yaw=None, states=None, verdict="only in " + ("A" if oid in a else "B")))
            continue
        frames = sorted(set(a[oid]) & set(b[oid]))
        missing = len(set(a[oid]) ^ set(b[oid]))
        first, total, max_d, max_yaw, states, identical = None, 0.0, 0.0, 0.0, 0, missing == 0
        for fr in frames:
            ra, rb = a[oid][fr], b[oid][fr]
            d = math.dist((float(ra["e"]), float(ra["n"]), float(ra["alt"])),
                          (float(rb["e"]), float(rb["n"]), float(rb["alt"])))
            dy = abs((float(ra["yaw"]) - float(rb["yaw"]) + 180.0) % 360.0 - 180.0)
            state = ra["pose"] != rb["pose"] or ra["ride"] != rb["ride"] or ra["beam"] != rb["beam"]
            same = d == 0.0 and dy == 0.0 and not state and ra["speed"] == rb["speed"]
            if not same:
                identical = False
                if first is None:
                    first = fr
            total += d
            max_d = max(max_d, d)
            max_yaw = max(max_yaw, dy)
            states += state
        out.append(dict(id=oid, kind=kind, frames=len(frames), first=first, max=max_d,
                        mean=total / max(1, len(frames)), yaw=max_yaw, states=states, missing=missing,
                        verdict=verdict(identical, max_d)))
    return out


def shots_of(path):
    if os.path.isfile(path):
        return {os.path.basename(path).split(".")[0]: path}
    return {f.split(".")[0]: os.path.join(path, f) for f in sorted(os.listdir(path)) if f.endswith(".tracks.csv")}


def fmt(v, unit=""):
    if v is None:
        return "-"
    if isinstance(v, float):
        return f"{v * 100:.1f} cm" if unit == "m" and v < 1 else (f"{v:.2f} m" if unit == "m" else f"{v:.2f}{unit}")
    return str(v)


def main(argv):
    args = [x for x in argv if not x.startswith("--")]
    markdown = "--markdown" in argv
    want_shots = want_kinds = None
    for i, x in enumerate(argv):
        if x == "--shots":
            want_shots = {f"shot{int(s):02d}" for s in argv[i + 1].split(",")}
        if x == "--objects":
            want_kinds = set(argv[i + 1].split(","))
    if want_shots or want_kinds:
        args = [x for x in args if x not in (argv[argv.index("--shots") + 1] if "--shots" in argv else None,
                                             argv[argv.index("--objects") + 1] if "--objects" in argv else None)]
    if len(args) != 2:
        print(__doc__)
        return 2
    sa, sb = shots_of(args[0]), shots_of(args[1])
    common = sorted(set(sa) & set(sb))
    if want_shots:
        common = [s for s in common if s in want_shots]
    for s in sorted(set(sa) ^ set(sb)):
        print(f"{s}: only in {'A' if s in sa else 'B'}")
    head = ["shot", "object", "kind", "frames", "first differing frame", "max", "mean", "max yaw", "state frames", "verdict"]
    if markdown:
        print("| " + " | ".join(head) + " |")
        print("|" + "---|" * len(head))
    else:
        print("  ".join(f"{h:>10}" for h in head))
    for s in common:
        for r in compare(sa[s], sb[s], want_kinds):
            cells = [s, r["id"], r["kind"], r["frames"], fmt(r["first"]), fmt(r["max"], "m"), fmt(r["mean"], "m"),
                     fmt(r["yaw"], "°"), fmt(r["states"]), r["verdict"]]
            if markdown:
                print("| " + " | ".join(str(c) for c in cells) + " |")
            else:
                print("  ".join(f"{str(c):>10}" for c in cells))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))

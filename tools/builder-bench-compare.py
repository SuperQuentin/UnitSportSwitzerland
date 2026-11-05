#!/usr/bin/env python3
"""Helper of builder-bench.sh (#1539): stage table from a build log, and the comparison of two runs.

  builder-bench-compare.py stage LOG            print stage.tsv (the [stage] lines of LOG, summed by name)
  builder-bench-compare.py compare DIR_A DIR_B  files that differ (exit 1) and a per-stage table
"""
import re
import sys
from collections import OrderedDict
from pathlib import Path

LINE = re.compile(r"\[stage\] name=(\S+) wall=([\d.]+) cpu=([\d.]+) cores=[\d.]+ gc_pause=([\d.]+) peak_ws_mb=(\d+)")
COLS = ["name", "n", "wall", "cpu", "cores", "gc_pause", "peak_ws_mb"]


def stages(log):
    out = OrderedDict()
    for line in Path(log).read_text(encoding="utf-8", errors="replace").splitlines():
        m = LINE.search(line)
        if not m:
            continue
        name, wall, cpu, gc, peak = m.group(1), float(m.group(2)), float(m.group(3)), float(m.group(4)), int(m.group(5))
        s = out.setdefault(name, dict(n=0, wall=0.0, cpu=0.0, gc_pause=0.0, peak_ws_mb=0))
        s["n"] += 1
        s["wall"] += wall
        s["cpu"] += cpu
        s["gc_pause"] += gc
        s["peak_ws_mb"] = max(s["peak_ws_mb"], peak)
    return out


def tsv(log):
    print("\t".join(COLS))
    for name, s in stages(log).items():
        cores = s["cpu"] / max(s["wall"], 1e-9)
        print(f"{name}\t{s['n']}\t{s['wall']:.2f}\t{s['cpu']:.2f}\t{cores:.2f}\t{s['gc_pause']:.2f}\t{s['peak_ws_mb']}")


def read_tsv(path):
    rows = OrderedDict()
    for line in Path(path).read_text(encoding="utf-8").splitlines()[1:]:
        f = line.split("\t")
        rows[f[0]] = dict(wall=float(f[2]), cpu=float(f[3]), cores=float(f[4]), gc=float(f[5]), peak=int(f[6]))
    return rows


def read_sha(path):
    d = {}
    for line in Path(path).read_text(encoding="utf-8").splitlines():
        h, _, p = line.partition(" ")
        d[p.lstrip(" *")] = h
    return d


def compare(a, b):
    a, b = Path(a), Path(b)
    sa, sb = read_sha(a / "sha256.txt"), read_sha(b / "sha256.txt")
    only_a = sorted(set(sa) - set(sb))
    only_b = sorted(set(sb) - set(sa))
    diff = sorted(p for p in set(sa) & set(sb) if sa[p] != sb[p])
    print(f"files: {a.name} {len(sa)}, {b.name} {len(sb)}; identical {len(set(sa) & set(sb)) - len(diff)}")
    for title, items in (("only in " + a.name, only_a), ("only in " + b.name, only_b), ("DIFFER", diff)):
        if items:
            print(f"{title}: {len(items)}")
            for p in items[:200]:
                print("  " + p)
            if len(items) > 200:
                print(f"  ... {len(items) - 200} more")
    ra, rb = read_tsv(a / "stage.tsv"), read_tsv(b / "stage.tsv")
    print()
    print(f"{'stage':<26}{'wall ' + a.name:>14}{'wall ' + b.name:>14}{'speedup':>9}{'cpu ' + a.name:>12}{'cpu ' + b.name:>12}{'cores A':>9}{'cores B':>9}")
    for name in list(ra) + [n for n in rb if n not in ra]:
        x, y = ra.get(name), rb.get(name)
        label = ("  " + name) if "/" in name else name
        f = lambda r, k, fmt: format(r[k], fmt) if r else "-"
        sp = f"{x['wall'] / y['wall']:.2f}x" if x and y and y["wall"] > 0 else "-"
        print(f"{label:<26}{f(x, 'wall', '.1f'):>14}{f(y, 'wall', '.1f'):>14}{sp:>9}{f(x, 'cpu', '.1f'):>12}{f(y, 'cpu', '.1f'):>12}{f(x, 'cores', '.2f'):>9}{f(y, 'cores', '.2f'):>9}")
    for r in (a, b):
        p = r / "summary.txt"
        if p.exists():
            print(f"\n{r.name}: " + " ".join(p.read_text().split()))
    return 1 if (only_a or only_b or diff) else 0


if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "stage":
        tsv(sys.argv[2])
    elif len(sys.argv) == 4 and sys.argv[1] == "compare":
        sys.exit(compare(sys.argv[2], sys.argv[3]))
    else:
        sys.exit(__doc__)

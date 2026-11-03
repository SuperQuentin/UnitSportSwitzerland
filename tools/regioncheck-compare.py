#!/usr/bin/env python3
"""Compare --regioncheck tracks (#1456): each site's walker and blocker in a run with both sites
against the same site run alone. Prints the largest difference per site and fails past the tolerance.

    regioncheck-compare.py DIR [--tolerance 0.01] [--before]

DIR holds both-a.csv and both-b.csv (one run, both sites), a-a.csv and b-b.csv (each site alone).
--before also reports the run without regions (both-noregions-*.csv) against the solo runs, and
site b against site a (the same walk on two grounds): what a far frame costs."""
import csv
import sys
from pathlib import Path


def load(path):
    with open(path, newline="") as f:
        return [[float(v) for v in row.values()] for row in csv.DictReader(f)]


def worst(a, b):
    """The largest horizontal-and-vertical difference between two tracks, step by step (metres)."""
    if len(a) != len(b):
        return float("inf")
    d = 0.0
    for ra, rb in zip(a, b):
        for i in range(1, len(ra)):
            d = max(d, abs(ra[i] - rb[i]))
    return d


def main():
    args = sys.argv[1:]
    folder = Path(args[0])
    tol = float(args[args.index("--tolerance") + 1]) if "--tolerance" in args else 0.01
    failed = False
    for site in ("a", "b"):
        together, alone = folder / f"both-{site}.csv", folder / f"{site}-{site}.csv"
        d = worst(load(together), load(alone))
        ok = d <= tol
        failed |= not ok
        print(f"site {site}: with the other site vs alone, worst {d * 100:.3f} cm {'ok' if ok else 'FAILED'} (tolerance {tol * 100:.1f} cm)")
    print(f"sites b vs a (the same walk 300 km apart, on two different grounds): worst {worst(load(folder / 'both-a.csv'), load(folder / 'both-b.csv')) * 100:.3f} cm")
    if "--before" in args:
        for site in ("a", "b"):
            before = folder / f"both-noregions-{site}.csv"
            if before.exists():
                print(f"no regions, site {site} vs alone with regions: worst {worst(load(before), load(folder / f'{site}-{site}.csv')) * 100:.3f} cm")
    print("RESULT: " + ("FAILED" if failed else "ok"))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())

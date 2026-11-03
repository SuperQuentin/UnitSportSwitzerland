#!/usr/bin/env python3
"""Compare two tools/test.sh result files (#1429): what a change broke, against a baseline.

    tools/test-compare.py BASE.csv NEW.csv [--flaky FLAKY.txt] [--slower 2.0]

Each file is the `results.csv` a run writes (TEST_LEDGER=). Rows are matched by (tier, check).
Reports, worst first:
    regressed   PASS in BASE, FAIL or TIMEOUT in NEW
    fixed       not PASS in BASE, PASS in NEW
    new / gone  in only one of the two
    slower      PASS in both, NEW more than --slower times BASE's seconds (and 20 s longer)
A check listed in FLAKY (one check per line, as the CSV has it; # comments) is reported but never
counts as a regression. Exit code 1 if anything regressed, else 0.
"""
import csv
import sys


def load(path):
    rows = {}
    with open(path, newline="", encoding="utf-8") as f:
        lines = [line for line in f if not line.startswith("#")]
    for r in csv.DictReader(lines):
        rows[(r["tier"], r["check"])] = r
    return rows


def main(argv):
    args = [a for a in argv[1:] if not a.startswith("--")]
    opts = dict(zip(argv[1:], argv[2:]))
    if len(args) < 2:
        print(__doc__)
        return 2
    base, new = load(args[0]), load(args[1])
    flaky = set()
    if "--flaky" in opts:
        with open(opts["--flaky"], encoding="utf-8") as f:
            flaky = {line.strip() for line in f if line.strip() and not line.startswith("#")}
    slower = float(opts.get("--slower", 2.0))

    regressed, fixed, slow, flaky_hits = [], [], [], []
    for key in sorted(base.keys() & new.keys()):
        b, n = base[key], new[key]
        if b["result"] == "PASS" and n["result"] != "PASS":
            (flaky_hits if key[1] in flaky else regressed).append((key, b, n))
        elif b["result"] != "PASS" and n["result"] == "PASS":
            fixed.append((key, b, n))
        elif b["result"] == n["result"] == "PASS":
            bs, ns = float(b["seconds"] or 0), float(n["seconds"] or 0)
            if bs > 0 and ns > bs * slower and ns - bs > 20:
                slow.append((key, b, n))
    gone = sorted(base.keys() - new.keys())
    added = sorted(new.keys() - base.keys())

    def show(title, items):
        if not items:
            return
        print(f"\n{title} ({len(items)})")
        for key, b, n in items:
            print(f"  {key[0]:6} {key[1]:60} {b['result']:8} -> {n['result']:8} "
                  f"{b['seconds']:>5}s -> {n['seconds']:>5}s  {n['log']}")

    show("REGRESSED", regressed)
    show("failed again, listed as flaky", flaky_hits)
    show("fixed", fixed)
    show("slower", slow)
    for title, keys, side in (("only in BASE (gone)", gone, base), ("only in NEW", added, new)):
        if keys:
            print(f"\n{title} ({len(keys)})")
            for k in keys:
                print(f"  {k[0]:6} {k[1]:60} {side[k]['result']}")
    same = len(base.keys() & new.keys())
    print(f"\n{same} checks in both: {len(regressed)} regressed, {len(fixed)} fixed, {len(slow)} slower, "
          f"{len(flaky_hits)} flaky failed again")
    return 1 if regressed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))

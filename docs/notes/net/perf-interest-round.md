# Interest pass: each target read once per round (#221)

## Rule
- `InterestService.Evaluate` reads every player's position (`Where`), height above ground and ride
  kind ONCE per round into the parallel lists `_at`/`_agl`/`_ride` (index = `_targets` index;
  `_viewerIndex[vi]` maps a viewer to it). Pair loops index those lists; never call `Where`,
  `GlobalPosition` or `Ground` inside a viewer x target loop.
- `Together` is asked once per pair in the first loop; the near/far split reads `_together`.
- The line-of-sight delegate is cached (`_sight`); never pass a method group per pair (one
  delegate allocation per call).

## Why
Every 0.5 s the pass made ~3·N² native position reads, up to 2·N² ground lookups, N² delegate
allocations and 2·N² `Together` calls. Now N reads/lookups and N² cheap float maths.
`tools/loadtest.sh` (real terrain), before -> after this PR, back to back: 16 players busy p50 1.58 -> 1.58 ms,
p99 3.48 -> 2.78 ms; 32 players busy p50 3.43 -> 3.03 ms, p99 18.48 -> 6.68 ms, slow frames 24 -> 12.

## Same logic, preserved
- Same inputs, same order, same decisions: positions are taken in the same frame, the rules
  (`Interest.Relevant`, hysteresis, `MinFlipSeconds`, near radius x1.2) are untouched.
- Trap: a new per-target input (e.g. a crouch flag) goes in a per-round list next to `_ride`, not
  a node read in the pair loop.

## Migrating old code / open branches
- #269 (floating origin, LV95) rewrites `Where` to return a `GlobalPos` and judges each viewer's
  pairs in a frame anchored at the viewer. Merge: make `_at` a `List<GlobalPos>`, fill it with
  `Where(p)` once per target, keep its `Local(to)` per pair (cheap arithmetic), and compute
  `_agl` once per target as `to.Alt + 1 - Ground(to)` (it does not depend on the viewer). Its
  `_sight` field replaces the one here (same name, same role).
- `grep -n "Where(" src/Net/InterestService.cs` after a merge: only the fill loop should call it.

## How to check
`tools/test.sh quick` runs `--interestcheck`; `tools/loadtest.sh 32` server busy p50/p99.

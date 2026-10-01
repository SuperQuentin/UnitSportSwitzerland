# Racecraft: flat out, in a pack and in traffic

- **Racecraft** (`AutoPilot.Traffic`, #52): every step one lateral target (an offset from the racing
  line, `State.Lateral`) and one speed cap (`State.Cap`) from everyone about — the race's cars
  (`AutoPilot.Other(Position, Velocity, Wreck)`: a remote's replicated `WorldVelocity`, so "coming at
  me" and "going away at the same speed" are told apart; the old `Other` carried `|v|` only and never
  saw an oncoming racer) plus whatever `Sensed` finds on the road ahead.
  - **Coming the other way**, anywhere on the road, met within 5 s: keep 2.3 m from where it will be
    when we meet (its lateral position + its sideways speed × min(meet, 2 s)); not over yet and no
    time left to get over at 4 m/s → slow; no room at all → meet it crawling. No pass starts with one
    in sight.
  - **Stopped / wreck** in the way: round it on the roomier side at a walk, never into it (not yet
    clear sideways → stop 5 m short).
  - **Alongside or less than a car length clear**: keep a car's width (2.0 m) from it — that is the
    "no chop" rule: back across its nose only once it is more than a car length behind.
  - **Slower ahead in the way** (where the car is headed OR where it still is): pass beside it on a
    straight (R > 150 m over 60 m + v) or dive up the inside of the next bend (aggression > 0.3,
    within 15 m, a car's width on the inside here and at the apex); the lane the pass moves into must
    be clear, and until out beside it the car still follows it. Otherwise follow at 10 → 5 m
    (aggression). Off the line toward a bend's inside the speed is √(R'/R) of the profile's.
  - **Blocked edges are hard** (`RoomL/RoomR`): the line's room, +0.3 m only where the verge was
    surveyed safe, never where it is blocked (drop, wall, trunk, no data); `RaceLine.Widen` keeps the
    line 0.9 m (was 0.5) off a drop. On top, `EdgeGuard`: if the body 0.8 s ahead (lateral speed carried
    forward) would come within 0.35 m of a blocked edge anywhere from here to there, steer back by
    2 × the excess (≤ 0.8) and lift. Without it, cars in a pack still cut apexes and swung wide of
    kinks by ~1 m (blocked 43 m over the first 1.3 km of a 6-car pack; 0 with it).
  - No planned drift with a rival within 30 m (three cars drifting one corner took each other out).
- **Sensing** (`Sensed`): road-wide boxes along the route; static bodies met are added to the query's
  exclusions — a terrain body returns a hit PER FACE, and with 8 results a box the width of the road
  never saw the car behind them. A body is only reported from its second sighting (velocity known).
- **Traffic** (`World/Traffic`) yields only to what is within 1.8 m of its lane (was 2.6: it stopped
  for a racer on the other half, the racer stopped for it, forever).
- **Unsticking**: a car standing 10 s with the race on (a jam) or facing back 3 s after a spin is reset
  to the line at the first point ahead with nobody within 8 m (`ResetToLine`), logged with what the
  pilot saw. Resets are a crutch: with traffic 35 they happen (see the numbers).
- `--drivecheck` prints a `SUMMARY` line (finishers, share of race time at ≥ 95% of the local profile
  speed, metres off the tarmac, metres over a blocked edge, knocks by kind, passes, spins, mistakes,
  resets, out) and logs each knock with what the pilot saw (`AutoPilot.Seen`) and each blocked-edge
  excursion with its context.
- Measured, Mollendruz descent (`--drivecheck --at 2518038,1167321 --finish 8200 --cars 0,1,3,4,12,6`),
  before = a7af037 with the SUMMARY counters, after = #52 (default: per-car skill/aggression):

  | | finishers | at pace | off tarmac | blocked edge | knocks | passes | spins | resets | out | times |
  |---|---|---|---|---|---|---|---|---|---|---|
  | traffic 0, before | 6/6 | 45% | 810 m | 144 m | car 17 | 9 | 0 | 0 | 0 | 288.7-307.9 s |
  | traffic 0, after | 6/6 | 43% | 472 m | **0 m** | car 5 | 5 | 0 | 0 | 0 | 300.1-316.8 s |
  | traffic 0, after, `--skill 1 --aggression 0.5` | 6/6 | 40% | 845 m | 36 m | car 8, other 1 | 13 | 0 | 0 | 0 | 294.4-314.1 s |
  | traffic 35, before | 1/6 | 17% | 581 m | 332 m | traffic 162, car 49, other 2 | 14 | 0 | 1 | 5 | - |
  | traffic 35, after | 3/6 | 15% | 760 m | 96 m | traffic 24, car 8, tree 2, other 1 | 6 | 0 | 26 | 3 | - |

  Not solved: the blocked edge is 0 in one configuration only (36 m with everyone at aggression 0.5:
  a car tailgating at the start 1.9 m off its line on a straight with no contact, a 9° slide); ~4% slower
  than before (0.9 m off drops, no given-up room at blocked edges, skill); traffic 35 on a 6 m pass is
  still half a race of jams and resets, and a car entering from a side road at a junction (5207 m) is
  not sensed until it is on the route — one threw a car off at 118 km/h. Traffic is random, runs differ.
- Side effect: with the "no planned drift within 30 m of a rival" rule a two-car drift-car drivecheck
  that stays nose to tail never plans a drift and exits "FAILED" (no held drift) — the 6-car runs still
  hold drifts (final run: FD 5, FC 3, AE86 2, S13 0; before: 3, 2, 3, 2).
- Traffic around a race (#85: seeing, reacting, junctions, standing traffic, retirement): `docs/notes/world/traffic-and-races.md`.

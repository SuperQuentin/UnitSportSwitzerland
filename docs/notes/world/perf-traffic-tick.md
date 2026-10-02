# Traffic per tick: sampling, gap check, draw range, shared meshes (#221)

## Rule
- In `Traffic.StepCar`, sample the car's road (`_roadPos` / `_roadDir`, 51 `Route.At` calls) only
  inside the obstacle loop, after the distance skip (the `sampled` flag). Never sample it for every
  car up front: the local player is always an obstacle.
- The gap to the car ahead walks `_byX` (cars sorted by `Head.X` once per tick) within
  ±`GapWindow` (45 m), not all of `_cars`. A new car-to-car rule with a radius of at most 40 m can use
  the same window. A larger radius needs a larger `GapWindow`.
- Cars are drawn to `CarDrawn` (600 m; they live to 950 m) through `Unit(body, lamps, CarDrawn)`.
  Trains have no limit: seeing one cross the valley is the point of them.
- Get traffic meshes only from `TrafficMeshBuilder.Car` / `Carriage`. They are cached per look and
  shared, so never change one (set `MaterialOverride` on the instance instead).

## Why
PR PRNUM, `--perflog`, static camera over traffic at Mollendruz, 2 runs each (physics ms p50):

| traffic | before | after |
|---|---|---|
| 35 | 0.76, 0.77 | 0.70, 0.61 |
| 150 | 2.16, 1.93 | 1.57, 1.55 |

## Same logic, preserved
- Sampling: the samples were only ever read for obstacles inside the skip radius. They are now taken at
  the first such obstacle, so the result is identical. If the radius changes (the #159 branch raises
  300 m to 450 m), the lazy sample follows it with no further edit.
- Gap check: the minimum over the same cars, in any order. The window covers 40 m ahead and 1.6 m
  aside, plus a tick's travel (under 1 m at 33 m/s). During a tick, positions can drift from the sort
  order by that travel, and the 5 m margin covers it.
- Trap: a car moved more than ~2 m within a tick (re-placed mid-step) breaks the window. Re-sort
  `_byX` after such a move.

## Migrating old code / open branches
- `grep -n "foreach (var other in _cars)" src/World/Traffic.cs`: the gap check now walks `_byX`.
  `GiveWay` (#121, from #230) still loops every car (it watches up to 200 m, past the window), and
  only for a car within 40 m of a yield line. Keep its distance filter before `other.Route.At(0)`.
  ponytail: if it shows at traffic 150, walk `_byX` with a 200 m window there too.
- `grep -n "Route.At(4f \* (Behind - k))"`: the sampling must stay inside `if (!sampled)`, right
  after the skip. Branches that edit the skip line (#159 `feat/159-races-in-traffic-2`:
  `300f * 300f` to `450f * 450f`) resolve the conflict by keeping both: their radius, then the block.
- `grep -n "Unit(body, lamps"`: new car kinds pass `CarDrawn`. Callers of `TrafficMeshBuilder.Car`
  must not modify the returned mesh.

## How to check
`--trafficcheck --traffic 150` (cars and average speeds every 5 s), and a `--perflog` run
(`physics_ms` p50/p99 and `gc0` in `frames.csv`), before and after.

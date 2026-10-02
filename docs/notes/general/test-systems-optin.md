# Test systems opt-in: every probe boots only what it tests (#221)

## Rule

- **Every new probe or check declares its world and uses the lightest that works**, in this order:
  1. tier 0, no Godot: pure logic goes in plain C#, linked into `tests/UnitSportSwitzerland.Tests`;
  2. `--world flat`: `Core/TestWorld`, a flat collision plane at y 0 and a fixed sun, **no
     `ChunkManager`**, nothing else. For bodies, mounts, vehicles, hitboxes, replicated state;
  3. `--systems a,b` (names in `Core/Systems.All`): the client world with only those systems.
     Without `terrain` it streams the one-tile flat fixture instead of the map. UI checks use
     `--systems ui`;
  4. `--world fixture` or `--chunks fixture:<course>`: the client world on tiles built in code
     (`Terrain/Fixture`), every system but the generated fill. Roads, routes, traffic, teardown;
  5. the real map (`--chunks <dir>`): only when the map itself is the test (terrain data, a
     real course). Tier 3, never in `quick`/`net`.
- **Driving, racing and traffic checks run on a fixture course, never require the Col du
  Mollendruz** (or any real course): `--drivecheck --chunks fixture:hairpin --traffic 0`. Courses:
  `flat straight hairpin narrow junction verge lake` (`FixtureCourse`). A real course is an optional
  final tier-3 check on a racing change.
- **A probe that runs on `--world flat`** takes a nullable `ChunkManager`, asks the ground through
  `TestWorld.TryGround(_chunks, at, out y)` (the plane when null), places its `FootPlayer` with
  `player.DebugLaunch(pos, Vector3.Zero)` when `_chunks == null` (a body with no `Terrain` otherwise
  waits for terrain forever), and is listed in `TestWorld._Ready`.
- **A new world system must be switchable**: create it in `ClientWorld`/`ServerWorld` under
  `if (Systems.On(Systems.X))`, add its name to `Systems.All`, and make its users null-safe
  (`Instance?.`). Absent `--systems`/`--world`, `Systems.On` is always true: players get everything.
- Each check in `tools/lib/checkmap.txt` carries its world flags (`--synccheck --world flat`).

## Why

Measured in #260 (Windows, Godot 4.7.1 headless, no real terrain; before = no flags, the
generated world with every system), peak working set:

| Check | Before | After |
|---|---|---|
| `--synccheck --world flat` | 64.5 s, 2 318 MB | 55.5 s, 210 MB |
| `--ride bike,20 --world flat` | 31.3 s, 1 306 MB | 23.4 s, 210 MB |
| `--menucheck --systems ui` | 13.3 s, 887 MB | 11.9 s, 430 MB |
| `--leavecheck --world fixture` | 25.9 s, 1 333 MB | 22.6 s, 537 MB |
| `--drivecheck --chunks fixture:hairpin --traffic 0` | 70.7 s, 2 225 MB | 93.8 s, 693 MB |

Full table in `testing`. Parallel agents running 2 GB checks ran the machine out of RAM.

## Same logic, preserved

- No flag = today's boot, unchanged: `Systems.On` returns true for every name, `FixtureCourse` is
  null, the generated fill and the network cache are wired exactly as before.
- `--world fixture` keeps every system the player has (traffic, birds, interiors, occasions…) and
  only replaces the ground, so `--leavecheck --world fixture` still checks every singleton's teardown.
- A fixture source answers every asset of its tiles (empty, not null), and the client skips the
  streaming cache and `MergeCachedIndex` under it: a cache of real tiles from an earlier session
  must never fill a fixture course's gaps or horizon. Keep that if you touch the source chain.
- Fixture roads are cut at tile edges with the crossing point shared, and a road meeting another
  ends there: the lane graph links ends only (`LaneGraph.Build`). A new course must do the same.
- Traps: a system turned off is `null`; code that reaches a system's `Instance` without `?.` breaks
  only under `--systems`. Locals that probes receive (`birds`, `gathering`) are `null!` when off.
  `--systems` without `trains`/`traffic` sets `GameSettings.Current.Trains`/`TrafficCars` for the
  run (not committed).

## Migrating old code / open branches

- Rebasing a branch that adds a system to `ClientWorld`/`ServerWorld` boot: wrap it in
  `if (Systems.On(Systems.<Name>))`, or add a name to `Systems.All` if none fits. Conflicts are
  likely in the `ClientWorld._Ready` system-creation block (#197, #191, #188, #180, #169, #237) and
  `ServerWorld._Ready`'s chunk-source lines (#233, #237): keep both sides, then the wrap.
- A probe built as `new XProbe(_chunks, origin)` that does not need terrain: make the parameter
  `ChunkManager?`, replace `_chunks.TryGetHeight(p, out g)` with `TestWorld.TryGround(_chunks, p, out g)`,
  `_chunks.Source` with `_chunks?.Source`, add the `DebugLaunch` placement above, list it in
  `TestWorld._Ready`, and give its `checkmap.txt` row `--world flat`. Done so far: `--hitboxcheck`,
  `--synccheck`, `--ride`.
- A driving/traffic probe or note that says "at the Col du Mollendruz" / `--at 25...,11...`: run
  it with `--chunks fixture:<course>` instead (unchanged probe code: the course starts at the
  spawn) and say the real course is optional.
- `grep -n "new ProceduralWorld\|new LocalChunkSource" src/` outside `ClientWorld`/`ServerWorld`
  /`Swarm`: a new chunk-source chain must honour `Systems.FixtureCourse` the same way.

## How to check

- `tools/test.sh quick Core/Systems` (hitbox on flat, menucheck with `--systems ui`).
- `tools/test.sh unit` runs `FixtureCourseTests` (tile validity, road/ground agreement, splits).
- `--drivecheck --chunks fixture:hairpin --traffic 0` must reach `[drive] SUMMARY`.

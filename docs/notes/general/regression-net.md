# Regression net: a baseline before a coordinate or rendering change (#1429)

Before a change that can move things it does not mean to (the coordinate system, the world
shaders, the floating origin: #187's Earth curvature was the first), take a baseline on `main`,
make the change, take the same again on the branch, and compare. Two halves: the checks, and the
pictures.

## The checks: a ledger and `coords`

- Every `tools/test.sh` run writes `test_output/tests/results.csv` (`TEST_LEDGER=<path>` elsewhere):
  `tier,check,result,seconds,log,note`, one row per check, a `# tools/test.sh ... commit ... date`
  line on top.
- `tools/test-compare.py BASE.csv NEW.csv [--flaky flaky.txt] [--slower 2]` lists what regressed
  (PASS -> FAIL/TIMEOUT), what got fixed, what is new or gone, what got more than 2x (and 20 s)
  slower. Exit 1 on a regression. A check in `flaky.txt` (one per line, as the CSV names it) is
  reported but never counts.
- `tools/test.sh coords` runs every checkmap row, all tiers, whose prefix covers code that uses
  the coordinate system: a grep at run time for `GlobalPos`, `WorldOrigin`, `OriginFrame`,
  `ToWorld(`, `ToGlobal(`, `ToLv95(`, `TileAt(`, `IOriginShiftAware`, `HorizonGround`,
  `horizon.bin`, plus `shaders/` and `src/Styles/`, and every `@rpc` row (positions cross the
  wire). 273 checks when it was added (172 quick, 82 net, 19 full); `TEST_LIST=1` prints the
  selection and stops. The full rows want `CHUNKS=D:/UnitSportSwitzerland/terrain_chunks`.
- Baseline: run it on the `main` commit the branch starts from, rerun each FAIL/TIMEOUT alone
  (twice) to tell broken-on-main from flaky, keep `results.csv` and `flaky.txt` together
  (`D:/UnitSportSwitzerland/baselines/<commit>/`). On the branch, the same, then
  `tools/test-compare.py`.

## Checks added for the gaps (#1429)

The coverage map found far rendering, GPX and the origin under load with no check at all:

- `--horizoncheck` (`src/Terrain/HorizonCheck.cs`, `--horizon 20 --systems terrain,generated`; the test profile has no horizon, #1411): every horizon
  block at its LV95 corner to the millimetre, none twice, none past the unload slack, the
  coverage texture's corner right, before and after three origin shifts (whole km, a fraction of a
  metre, back).
- `--gpxcheck` (`src/Gpx/GpxCheck.cs`, `--systems terrain,generated`): a 1.5 km course built in
  code, its ribbon's tread on the ground under it, `Runner.CourseAt` on the course's LV95 point,
  the runner's feet on it, across two shifts, and no jump in LV95 between frames.
- Mapped probes that existed: `--trafficcheck --chunks fixture:junction`, `--birdcheck` and
  `--birdstrikecheck --world fixture`, `--voidcheck --world fixture`. `--treecheck` is not mapped:
  on `fixture:verge` the bike passes 0.93 m beside the trunk it aims at, and without a fixture
  course it never ends.
- The origin-stress pack, under `src/Core/OriginShifter` and `src/Core/WorldOrigin`:
  `--ride car`, `--boatcheck speedboat` and `--flycheck a320` with `--originstress 20`,
  `--drivecheck` (hairpin) with `--originstress 200`. At 20 the BNR32 spun five times in one run
  of two (a shift twice a second at 160 km/h); a shift every 3 km in play is far from that.
  `--swimcheck` is not stress-safe: it measures its strokes and its beach from world `Vector3`s, so
  under stress it reads 5.5 m/s swimming and wakes "18 m" away (`core/floating-origin`: give it a
  `GlobalPos`).
- `--gpxcheck` found a real bug on the way: `Runner.UpdateTo` threw "Argument is not normalized"
  every frame once its heading had settled on a dead straight course (`Vector3.Slerp` between two
  almost equal directions), so a road-matched course on a straight road stopped its runner.
- Long-range rules pinned as they are (flat Earth), for #187 to flip on purpose: `--interestcheck`'s
  "90 km over a level plain" case, and `PhotoJudgeTests.A_level_target_twenty_km_over_a_level_plain...`.

## The pictures: `tools/refshots.sh` and `tools/refshot-diff.py`

- `tests/refshots/views.txt` is the catalogue: ~27 views on the real terrain of D: (Geneva, Jura,
  Lausanne, Lavaux, Chablais, Riddes, and La Tzoumaz's high slopes in July and January since #1746), each with a class: `near` (< 1 km), `mid`, `far` (20 km
  and more), `edge` (far peaks at the frame's top edge, the horizon at its bottom: frustum
  culling), `night` (lamps; 1 August for the bonfires).
- `tools/refshots.sh <tag>` takes every view in `STYLES` (cartoon, ps1, real- by default), one
  launch per style and date, into `D:/UnitSportSwitzerland/refshots/<tag>/<style>/<view>.png`
  (`REFSHOTS=` elsewhere), and `meta.json` (commit, flags, GPU, time). The pictures never go in git.
  - Pinned like the trailer's renders (`trailer/render-pins`): a fresh `user://` per run (only the
    shader caches carry over), the default settings plus `RENDER`, `--date`, `--time`, `--origin`.
  - The waves are drawn at a fixed wave time (`--watertime 100`, `World/WaterSurface`): without it a
    lake shot changed 15-65 % of its pixels between two runs of the same commit. Fire and smoke
    models animate on the shader's own `TIME` and keep their floor (up to 26 %).
  - What moves by itself is left out (`SYSTEMS`: no traffic, trains, NPCs, birds, loot, network,
    audio), and the run is muted (`--audio-driver Dummy`).
  - `MODELS=1` adds the model viewer's PNG run per style (`<style>/models/`, ~1000 models):
    figures, vehicles, props and buildings close up, the near shaders' best check.
  - `ARGS="--curvature 0"` passes game flags: a branch's picture with its change switched off
    must match `main`'s.
  - `VIEWS="a b"` retakes only those (a crash, a fix), into a tag of its own, then copied over. It
    keeps the catalogue's origin: from the subset's own, the floats round otherwise (8 % of a shot).
  - Compare only libraries taken with the same pins: a base taken before `--watertime` existed
    differed from the branch on every lake. Take the base again when the tool's pins change.
  - Windowed (headless renders nothing): it opens a window per launch at 24,48, never on top.
- `tools/refshot-diff.py A B [--noise noise.json]` compares two libraries picture by picture:
  mean difference, the share of pixels off by more than 8/255, the largest difference; a heatmap
  and an A | B | diff sheet per changed shot; `report.html` grouped by class (models apart) and
  `summary.md` for the PR; exit 1 when a shot is over its threshold.
- **The noise floor first**: two libraries of the same commit
  (`main-<commit>-a`, `-b`), then `refshot-diff.py a b --write-noise .../noise.json`. Each shot's
  threshold is twice its floor plus 0.2 % (0.5 % without a floor). A shot that is noisy on its own
  needs its source pinned, not a bigger threshold.
- Reading a diff after a rendering change: `near` and `models` should stay under their floors;
  what the change is about shows in `far`/`edge`/`night`, and is judged by eye in the report.

## Earth against flat: G2 C6's gate (#1639, #1500)

`--frame earth` must look at least as good as the flat world before D flips it on. Measured, not eyeballed:

```bash
tools/refshot-parity.sh c6-<commit>
```

- One heavy-run slot, ~50 min for three styles. It takes the flat library (`ANCHORS=1`), then the earth one
  (`tools/refshots.sh --frame earth`, B4's leaves and pyramid from `D:/UnitSportSwitzerland/test_b3/b4/`), then
  the diff: `D:/UnitSportSwitzerland/refshots/<tag>-earth/diff-<tag>-flat/` with `summary.md` (within the floor,
  over it, judged), `report.html` and `sheet-<style>.png` (A flat | B earth | difference, by class). Exit 1 when a
  shot not judged is over its floor. `SKIP=flat` retakes only earth against the same `<tag>-flat` (a fix that
  touches earth alone), `SKIP=shots` only the diff; `STYLES`, `VIEWS` as `refshots.sh`.
- **Same place, same direction** (`tools/refshot-poses.py`): the views go in groups round an anchor on a whole km
  (each within 6 km), one launch per group started there (`--at`, `--origin`), in both worlds. Under earth the
  world is the east-up-south frame at the anchor; each view's place goes LV95 + height -> ECEF -> that frame, its
  direction is taken in the flat world's axes at the place (grid east, up, grid north: `FrameAxes.GroundAt`) and
  turned into it. Geneva's views turn ~0.95° (the grid's convergence), the 400 km pitch stays -15°. A `g` view
  stands at the altitude its flat shot logged (`HEIGHTS=<flat library>`), so a wrong earth ground shows as a
  difference instead of moving the camera.
- **1/8 scale** (`refshot-diff.py --scale 8`): both pictures box-shrunk to 200 x 112 cells, so the leaves' tile
  patterns at another phase (#1512) do not count, and a wrong colour, a missing forest or a void do. Its floor is
  `D:/UnitSportSwitzerland/baselines/dd6b7207/noise-s8.json` (two flat runs of one commit at that scale).
- **Expected differences** are lines of `tests/refshots/earth-vs-flat.txt` (`refshot-diff.py --expect`): `judge`
  takes a view or `class:<c>` out of the count into "judged by eye" (`far` and `edge`: curvature is geometry on
  earth, #187; `space` until #1630/#1631), `mask x0,y0,x1,y1` leaves a part of the picture out of the numbers
  (grey on the heatmap). Each line says why; remove it once its difference is gone. `judge N%` judges a view only
  while its changed share is at most N %, so a new difference in a judged view still fails: `villeneuve_a9`'s
  tree seeds are 12.5 % under `judge 20%`, and Léman over its meadow (55 %, #1696) had slipped through a bare `judge`.
- **A fresh worktree** needs `git lfs pull` and a headless `--import` (with `dotnet build`) before any windowed
  shot: without them the fonts and the space and realistic textures are missing from every picture (the import
  marks an LFS pointer file `valid=false` and rewrites its `.import`; `git checkout -- assets` after a pull).
- `space` views (60 and 400 km, the limb and the ground straight down) are in the catalogue for both worlds.
  Both get the same pyramid, the game's (`Globe.DefaultDir`, `D:/UnitSportSwitzerland/cube_global`, rebuilt from B4's
  leaves by #1654); `GLOBE` overrides it. Until Nov 10 2026 earth took `test_b3/b4/cube_global`, older, and missed
  #1654/#1669's fixes.
- The gate holds when `summary.md` has no shot over its floor and a person has looked at every judged sheet and
  found earth no worse. Each C6 fix session reruns it (`SKIP=flat` against the last flat library of the same
  commit is enough when only earth changed); D records the last run in its PR.

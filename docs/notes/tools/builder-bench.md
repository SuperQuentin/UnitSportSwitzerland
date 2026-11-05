# Terrain builder benchmark (#1539)

A speed-up of the terrain builder is only accepted when it is faster **and** every output file is
byte-identical. `tools/builder-bench.sh` builds one fixed region with a published binary and records both.

- Region: Geneva city and lake (km tiles E 2498-2503 x N 1116-1121, 36) and Valais mountains and Rhone
  valley (E 2582-2585 x N 1110-1113, 16), newest swissALTI3D survey per tile, Léman bed only
  (`BENCH_LAKES`, default `lacleman`). The pipeline is `tools/rebuild-map.sh all` for it: terrain +
  horizon, OSM overlay, places + POIs, roads, buildings, road network, cover + trees, water, landings,
  airports, and the fingerprint hash after each pass.
- Bench folder `BENCH` (default `D:/unitsport_bench`, else `/tmp/unitsport_bench`): `src/` and `bathy3d/`
  (copied once, so the NAS is not timed), `bin/<label>/` (published), `runs/<label>/`.
  Source data comes from `UNITSPORT_DATA` (default `ressources/data`; `UNITSPORT_BATHY` for the lake zips).
- Steps: `prepare`; `publish <label> [worktree]`; `run <label> <label-or-bin-dir> [--jobs N] [--tiles FILE [--src DIR]]`
  (about 5 min on a 6-core Ryzen with all cores; `--tiles` runs a small tile list for a sanity check,
  copying its own sources); `compare <A> <B>` exits non-zero and lists differing files, then prints the
  per-stage wall, CPU and cores table with the speed-up. Run it from the checkout whose
  `docs/data/cover_overrides.json` should be used: the builder reads it relative to the working directory.
- Rural region: E 2591-2598 x N 1094-1120 (216 km tiles: Sion, the Val d'Herens, Lac des Dix, Cheilon glacier), `--tiles` a list of them with `--src` a kept copy of their sources, so that runs compare (4.4 GB). Mountains and big polygons, which the
  benchmark's mostly urban tiles do not weigh: 92 s a run before #1539, cover first (35.7 s), network second (20 s).
- Multi-batch region: E 2538-2567 x N 1136-1152 (477 tiles built, 400 + 77), sources kept in `multi/src` (7 GB): the Léman shore, Vevey, Montreux and the Alps behind; 144 s a run on this branch, 405 s on `main`, 21-24 GB peak.
- Timer: `StageClock` prints `[stage] name=cover wall=.. cpu=.. cores=.. gc_pause=.. peak_ws_mb=..` per
  stage, always. `SetStage` in `Preprocessor.cs` starts a top-level stage; `StageClock.Sub("stage/phase")`
  times phases inside it (roads, cover, water). A run folder holds `log.txt`, `stage.tsv` (lines summed by
  name), `sha256.txt` (every file of `chunks` and `chunks_temp`, sorted) and `summary.txt`.
- Exclusions (`EXCLUDES` in the script, listed in each run's `sha256.excluded.txt`): both differ between
  identical runs for a reason that is not output.
  - `chunks_temp/osm_overlay_report.txt`: its `timing:` line holds seconds and the peak working set.
  - `chunks_temp/tile_hashes.tsv`: holds each file's mtime; the tiles' content hashes are in `manifest.json`.
- The `<E>_<N>.edge` files in `chunks_temp` carry the source zip's mtime: runs sharing `bench/src` agree,
  a `--tiles` run copies its sources and gets other mtimes.
- The baseline predates RoadGen's Server GC; the Preprocessor had it all along, so only standalone `--rewrite` runs differ (`roadgen-parallel`).

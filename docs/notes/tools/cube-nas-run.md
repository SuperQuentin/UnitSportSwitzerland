# The earth leaves on the NAS (#1794)

`tools/build-swiss-nas.sh cube ...` builds a region's earth leaves (cube-sphere L14 `.cubl` bundles and
`cube_manifest.json`, G2) **on the TrueNAS box**, from the raw data under `data/`, into `terrain_chunks_cube`
(+ `terrain_chunks_cube_temp`, log `terrain_chunks_cube.log`). It is the game server's ground for D2 (#1727): a release
ships no terrain, so players stand on the leaves the server streams (`general/linux-deploy`, the D2 runbook). Same
runner as `swiss-nas-run` and `france-nas-run` (self-contained linux-x64 publish copied by `tar | ssh`, `setsid nohup
nice ionice`, pidfile, `bash -s`, no root); the stages are `tools/lib/cube-nas-chain.sh`.

```
tools/build-swiss-nas.sh cube check         # the sources every stage reads, on the NAS; builds nothing
tools/build-swiss-nas.sh cube test          # every stage on D1's Geneva box (9 leaves), foreground: run this first
tools/build-swiss-nas.sh cube start         # publish, check, run every unfinished stage detached (again = resume)
FROM=network tools/build-swiss-nas.sh cube start   # redo that stage and the later ones
tools/build-swiss-nas.sh cube status | stop | log [N]
```

- **Where**: `NAS` (ssh target) and `NAS_ROOT` (the project folder on the NAS) from `tools/deploy.env` or the environment
  (`tools/swiss-nas-run`, settings); `NAS_HEAP_GB` and `NAS_JOBS` too.
- **The region**: `TILES=<file>` (LV95 km tiles, `E-N` a line, `#` comments), default `tools/regions/d2-server.tiles`:
  the server's region, B4's 825 tiles, Geneva to Riddes (#1508). Its `--cube-box` is the tiles' bounds, as MapSetup's
  `Selection.Bounds()`. Or `BOX=E0,N0,E1,N1` in LV95 metres: the leaves whose centre is inside, with the tiles within
  1 km of the box as their sources. `CUBE_OUT=` another output (a second region, #1650's nationwide run).
- **Why the tiles are linked**: the heights stage (`--cube`) reads every swissALTI3D zip under `--in` and makes a leaf
  wherever they reach inside the box. The NAS holds all of Switzerland, so the selection's 0.5 m zips (newest flight of
  each) are linked into `<out>_temp/alti_in`, as B4's data folder held only its 825 tiles. Otherwise the server's box
  (94 x 55 km) would be filled whole.
- **Stages**, MapSetup's `cube` layer (`Pipeline.AddCubeSteps`) as TerrainPreprocessor modes, never MapSetup itself (an
  interactive wizard). A finished stage leaves `<out>_temp/stage_<name>.done`:
  `heights` `--cube`, `roads` `--cube-roads`, `buildings` `--cube-buildings`, `sources` `--cube-sources` (the OSM overlay
  and tram stops into the temp), `places` `--cube-places`, `network` `--cube-network --frames-parallel FRAMES`, `cover`
  `--cube-cover`, `water` `--cube-water`, `covernet` `--cube-cover-network`, `trees` `--cube-trees`, `fields`
  `--cube-fields`, `pistes` `--cube-pistes`, `files` `--cube-region-files`.
  **No pyramid** (`--cube-swiss`): the server must not get `cube_global` (`general/linux-deploy`, fill agreement).
- **Sources**, checked before anything runs (`cube check`, and again by `cube start`/`test`): a missing one stops the run
  with exit 3 and the `tools/swiss_data.py` line that fetches it (run it from the repo with `--out` on the share,
  `X:\UnitSportSwitzerland\data\...`, or on the NAS with `python3`). Required: swissALTI3D 0.5 m for every listed tile
  (`MISSING_TILES_OK=1` for tiles that have none: lake, abroad; a box's margin tiles are never fatal), swissTLM3D
  (`data/tlm3d/*.gpkg`), the GWR (`data/gwr/data.sqlite`), swissBUILDINGS3D (`buildings_ch.gpkg`, else the newest
  flight of every sheet zip), OpenStreetMap (`data/osm/switzerland-*.osm.pbf`; `NO_OSM=1` builds without: no OSM lanes
  or signals, no pistes). Optional, reported: swissBATHY3D (synthetic beds without), LWB (OSM fields only),
  swissBOUNDARIES3D in `data/boundaries/` (POI cantons from the nearest town). `ALTI2M=1` / `RGEALTI=1` add the 2 m
  corridor and IGN RGE ALTI heights (off: B4 had neither).
- **Memory**: `HEAP_GB` 20 for cube (B4 peaked 10.8 GB with one network frame; the 2 m corridor's water 13.0 GB);
  `FRAMES` (network frames side by side, ~5 GB each) defaults to `(HEAP_GB - 6) / 5`, 1-4: 2 at 20 GB.
- **Resume**: a stopped or killed run restarts at its unfinished stage, which starts over and writes its own sections
  again. The selection is linked again on every start.

## What to expect on the NAS for the server's region

B4 (#1508, the same 825 tiles through MapSetup on the user's PC, 8 cores, one network frame): 2,412 leaves, 775 MB,
1 h 46 min, of which the network stage 86 min; 10.8 GB peak (MapSetup's one process hosting every stage; here each stage
is its own process, so the peak is the largest stage's). Since B4 the build also writes fields, pistes, POIs and
ropeways, so expect a little more. Fixed costs that do not shrink with the region: the building sheets' bounds and the GWR
(~1-2 min), the region files' nationwide POI scan (~4-6 min). Disk: the heights stage's cache (`<out>_temp/cube_cache`,
decoded swissALTI3D) reached 17 GB for the server's region in WSL; the leaves are ~0.8 GB. Copy to the server with
`tools/deploy-linux.sh --leaves` and `LEAVES_SRC=X:/UnitSportSwitzerland/terrain_chunks_cube` (the D2 runbook in
`general/linux-deploy`).

## Checked without the NAS (Nov 13 2026, #1794)

The real runner, with a stub `ssh` that runs the NAS side in WSL Ubuntu (as a transient `systemd-run --user` unit, so a
detached chain outlives the call as under sshd) and `data/` linked to D:'s sources (the D: region's 825 tiles, 82 building
sheets, the GWR, the 2026 TLM, OSM, Lake Geneva's bathymetry, no LWB).

- `cube check`: every source found; with an empty `data/` and a tile outside the data, five MISSING lines with their fetch
  commands and exit 3, nothing built.
- `cube test` (Geneva, linux-x64, 8 threads): every stage, 9 leaves, 591-804 s wall (D:'s sources through WSL's 9p mount;
  the TLM-reading stages are slow there). Peaks: sources 1.8 GB, network 1.8-2.2 GB, fields 1.6 GB, buildings 0.9 GB, the
  rest under 0.6 GB. Region files PASS (every point back to LV95 at 0.000 cm).
- **Linux against Windows**, the same stages, box and sources: heights, cover, holes, water, trees, places and ropeways
  byte-identical; every network record matched (approaches, junctions, poles, signals, stop lines: same look, 0.00 cm).
  Roads, buildings, landings, airports and POIs differ only in the last float digit (landing heights up to 1.9e-7 m,
  latitudes 5e-14°): .NET's `Math.Sin/Cos/Atan2` come from each OS's C library. Not byte-identical across OSes, then;
  identical on one.
- **Found and fixed**: the signal cantons (`TileRewriter.Cantons.Find()`, Geneva's 2-lens pedestrian heads, the Romandy
  tram T, plates, pole colours) were looked for only as the repository's `tools/MapCore/switzerland.bin` upward from the
  tool or the working directory. A published tool on the NAS has none, so every junction kept the default heads with no
  word. RoadGen now embeds the file and falls back to it; the network stage logs `cantons (...): <source>`.
- **Box mode** links the tiles within 1 km of the box: a leaf whose centre is inside reaches ~430 m past it, and with only
  the box's tiles 6 of the 9 Geneva leaves had other heights.
- **Against D1's Geneva build** (`D:/UnitSportSwitzerland/test1711/br/gva`, Windows, Nov 10): every section the same but
  the roads of 3 leaves (13 more poles, 8 moved: network changes since). **Against B4** (Nov 5): every kind differs in
  bytes; the ground of a dry leaf is the same to the centimetre at every sampled vertex (`--cube-dump`), a lake leaf
  differs at one bed vertex by 1.97 m (the water stage's #1577/#1677 since). The digest's roads: junctions 543/544 matched,
  signals 53/55.
- **Stop and resume** (Geneva box, on D:): `cube stop` during buildings (SIGTERM, the log's `stopped`), then `cube start`
  again. The selection was linked again and the sources checked for the 11 unfinished stages only. Heights and roads
  were `done earlier, skipped`, buildings started over, and the run ended with `ALL STAGES DONE`: 9 leaves, network 543
  junction records and 53 signals, as in the uninterrupted run. Through D:'s 9p mount the TLM-reading stages were slow
  (buildings' setup 822 s, fields 745 s, region files 1689 s), which the NAS's local disks should not be.
- **After main's #1822** (a junction-cap fix in the network stage), `cube test` again: every stage, region files PASS,
  cantons from the built-in copy, 550 junction records and 53 signals (543 before: #1822's change), 1825 s on D:.
- Not run here: the whole server region on Linux (2 h and 18 GB of cache; B4's numbers stand in), the real NAS.
- `--cube-dump` writes its JSON into the region folder: never point it at a reference set.

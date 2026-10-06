# Region setup wizard

- **Two projects since #515**: `tools/MapCore/` (net8 library, `UnitSport.Map`) holds everything that
  is not a user interface — `CountryData`, `Selection`, `LocalState`, `Paths`, `Stats`, `Planner`/`Step`,
  `StepRun` — and is referenced by **both** the terminal tool and the game's own map screen.
  `tools/MapSetup/` is only the terminal UI on top of it (`Program`, `MapView`, `Bake`, `Snapshot`).
  A step reports through `IStepProgress` (`SpectreProgress` in the terminal tool) rather than a
  Spectre `ProgressTask`, which is what lets the game drive the same steps.
  `switzerland.bin` lives in `tools/MapCore/` and is an **embedded resource** of that assembly, so the
  game reads it with no data path or export filter involved; `CountryData.LoadPreferringFile` still
  prefers a loose copy, which is what `--bake` writes.
- **Downloads are C#, not Python, since #515**: `MapCore/SwissDownload` talks to the swisstopo STAC
  API itself for **swissALTI3D tiles, swissTLM3D and GWR** (`AltiAsync`, `TlmAsync`, `GwrAsync`),
  with `swiss_data.py`'s own parallel jobs, byte-range fetches for big files, `.part` resume and
  SHA-256 verification against `checksum:multihash`. It shares that tool's per-directory
  `.swiss_data_manifest.json` byte for byte, so a machine that has used either one never
  re-downloads the other's files. TLM picks the **latest release** by `datetime` and prefers
  `.gpkg.zip`; GWR is not STAC at all but one zip per canton at
  `public.madd.bfs.admin.ch/<canton>.zip`, with no published checksum, so its skip decision falls
  back to size/ETag/Last-Modified. **swissBUILDINGS3D** (#564) tells per-sheet items from the one
  nationwide asset by how much of the country the item spans, keeps only sheets whose **LV95
  footprint** really touches the wanted tiles (its lon/lat bbox is tens of metres too big on every
  side, which would pull in the neighbours), and takes the newest year of each sheet. **OSM** is not
  STAC at all: the newest dated `switzerland-YYMMDD.osm.pbf` on Geofabrik's index, never `-latest`,
  which has answered with a redirect to itself. Nothing in the pipeline shells out to Python any
  more; `swiss_data.py` stays as the standalone tool.
- **The preprocessor runs in-process since #515**: it targets net8 (like the game) and
  `Preprocessor.RunAsync` runs the same argument-driven pipeline as its CLI, so neither the game nor
  this tool needs a .NET SDK or a subprocess to build a tile; `StepRun.Tool` calls it directly and
  `IPreprocessorLog` replaces scraping stdout. Verified by output identity: the same four tiles
  built in-process and by the old net9 CLI are byte for byte identical. RoadGen is still a
  subprocess (top-level statements, no library seam) and its step is skipped in practice, so a
  rootless `Paths` fails it with a clear reason rather than inventing a path.
- **Region setup wizard**: `dotnet run --project tools/MapSetup` (`tools/MapSetup/`, Spectre.Console).
  Terminal map of CH (raw 24-bit ANSI, half-block pixels) to select tiles (rectangle, brush, town +
  radius, canton), an estimate table (download / disk / time per step), then it chains the whole
  pipeline below as subprocesses. Every step skips when its output exists, and the state lives in
  `terrain_chunks_temp/mapsetup*.json`. The map comes from the committed
  `tools/MapCore/switzerland.bin` (per-km tile: zip size, survey year, canton, max elevation;
  places; buildings sheets; nationwide file sizes). `--bake` rebuilds it from STAC +
  swissBOUNDARIES3D. Non-interactive: `--town X --radius km | --canton VS | --bbox E0,N0,E1,N1 |
  --tiles-file f | --resume`, `--layers`, `--plan-only`, `--yes`. The tile-list plumbing it relies
  on: `swiss_data.py --tiles-file/--progress-json`, TerrainPreprocessor
  `--features-only --tiles-file` and `--places-only` (places without re-running roads, which
  would strip junctions), RoadGen `--tiles-file --skip-rewritten`, and `export_buildings.py --src`
  (per-sheet zips).
- **Storage location**: `--pick-location` or "Storage location..." under "Go?" lists the repo's
  folders, every ready drive (`<drive>/UnitSportSwitzerland/{data,terrain_chunks}`, free space
  shown) and a typed folder, then asks whether to move the source data, the built tiles or both.
  Saved in `terrain_location.json`, which the game and the server read too
  (`docs/notes/terrain/data-location.md`).

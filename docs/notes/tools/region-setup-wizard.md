# Region setup wizard

- **Region setup wizard**: `dotnet run --project tools/MapSetup` (`tools/MapSetup/`, Spectre.Console).
  Terminal map of CH (raw 24-bit ANSI, half-block pixels) to select tiles (rectangle, brush, town +
  radius, canton), an estimate table (download / disk / time per step), then it chains the whole
  pipeline below as subprocesses. Every step skips when its output exists, and the state lives in
  `terrain_chunks_temp/mapsetup*.json`. The map comes from the committed
  `tools/MapSetup/switzerland.bin` (per-km tile: zip size, survey year, canton, max elevation;
  places; buildings sheets; nationwide file sizes). `--bake` rebuilds it from STAC +
  swissBOUNDARIES3D. Non-interactive: `--town X --radius km | --canton VS | --bbox E0,N0,E1,N1 |
  --tiles-file f | --resume`, `--layers`, `--plan-only`, `--yes`. The tile-list plumbing it relies
  on: `swiss_data.py --tiles-file/--progress-json`, TerrainPreprocessor
  `--features-only --tiles-file` and `--places-only` (places without re-running roads, which
  would strip junctions), RoadGen `--tiles-file --skip-rewritten`, and `export_buildings.py --src`
  (per-sheet zips).

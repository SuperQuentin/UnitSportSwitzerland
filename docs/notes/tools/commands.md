# Commands

- Preprocess: `dotnet run --project tools/TerrainPreprocessor -c Release -- --in ressources/data/swiss_chunks --out terrain_chunks --verify --dump-png terrain_chunks_png`
  `--in` is recursive and repeatable (sources on any drive/share), `--jobs` defaults to all cores,
  `--io-jobs` (4) caps concurrent source reads. **One pass, no parse cache**: `TerrainBuild`
  reduces each tile straight to its interior vertices plus 16 KB of perimeter *partial sums*
  (`<temp>/E_N.edge`); a tile is written once every tile around it has published its partials, so
  the old 50 GB pass-1 `.raw` cache and its single-threaded, lock-per-cell pass 2 are gone.
  Output is byte-identical to the old pipeline (checked on all 6,699 tiles). Incremental: tiles
  with a valid `.terr` + `.edge` and an unchanged source (size + mtime) are skipped, and only the
  seams of their new neighbours are refreshed from the existing `.terr`. `--force` re-parses,
  `--fresh` drops tiles from earlier runs. Numbers are read by a fixed-point scanner
  (`mantissa / 10^d` is one correctly rounded division, i.e. the same double `Utf8Parser` gives);
  the tool targets net9.0 for its zlib-ng inflate.
- Coarse companion tiles (needed once for a region built before they existed; a normal build
  emits them): `dotnet run --project tools/TerrainPreprocessor -c Release -- --out terrain_chunks --coarse --jobs 8`
  — 6,699 tiles in 10 s, 3,207 MB read -> 33 MB written, every tile verified bit-identical.
- Far horizon (needed once for a region built before it existed; a normal build and `--coarse` emit
  it): `dotnet run --project tools/TerrainPreprocessor -c Release -- --out terrain_chunks --horizon --jobs 8`
- Buildings export (needs GDAL): `python tools/export_buildings.py --bbox 2578500 1108500 2586500 1115500`
- Features: `dotnet run --project tools/TerrainPreprocessor -c Release -- --out terrain_chunks --features-only --tlm <tlm.gpkg> --route-keys ressources/data/routes/route_keys.sqlite --cover --buildings ressources/data/buildings3d/buildings.gpkg --gwr ressources/data/gwr/data.sqlite`
- Roads preprocessing: `dotnet run --project tools/TerrainPreprocessor -c Release -- --out terrain_chunks --roads-only --tlm ressources/data/tlm3d/SWISSTLM3D_2026_LV95_LN02.gpkg --route-keys ressources/data/routes/route_keys.sqlite`
- Junctions (run **after** roads preprocessing, rewrites `.road` in place as v2):
  `dotnet run --project tools/RoadGen -c Release -- --rewrite --chunks terrain_chunks`
  (`--dry-run` to measure only, `--tiles "E,N;E,N"` to limit, `--force` to override the
  already-rewritten guard)
- Place index: `dotnet run --project tools/TerrainPreprocessor -c Release -- --out terrain_chunks --features-only --places --gwr ressources/data/gwr/data.sqlite --tlm <tlm.gpkg>`
  (`--tlm` adds named summits and passes; without it the index is towns only). **This also
  re-runs roads, which strips the junction polygons — always finish with `RoadGen --rewrite`.**
  writes `places.json`; **Tab** in game opens the teleport search (`PlaceSearchUi`). A
  municipality is placed at its **densest 500 m GWR cell**, not its centroid — communes
  stretch up the hillside, so the centroid of Riddes lands on the mountain above it.
- French features for a box (needs the terrain built there already; merges into existing tiles):
  `dotnet run --project tools/TerrainPreprocessor -c Release -- --out terrain_chunks --france 6.21,46.26,6.27,46.30`
- Cover only, for a few tiles (after editing `docs/data/cover_overrides.json`; run from the repo
  root): `dotnet run --project tools/TerrainPreprocessor -c Release -- --out terrain_chunks --cover-only --tlm <tlm.gpkg> --tiles-file tiles.txt`
  (`tiles.txt`: one `E-N` per line). Skips the road stage, so the RoadGen junctions survive.
  It rewrites the tile's `.trees` too, and a tile whose roads were rewritten by RoadGen after its
  first cover pass gets a reshuffled (equally dense) scatter: the road mask changed under the RNG.

# Bathymetry: lake and river beds, the `.water` level layer (#298)

swissALTI3D models water as its flat surface, so before #298 the terrain at a Water cover vertex
*was* the water level (`terrain/water`). Now the `.terr` heights under water are the **bed**, and
the level is its own per-tile layer. Runtime use of the level (surface, waves, `TryGetWaterLevel`)
is #299: `terrain/water-level-layer`.

## Data

- **swissBATHY3D** (swisstopo, open data): STAC collection `ch.swisstopo.swissbathy3d`, **one item
  per lake** (23: `swissbathy3d_lacleman`, `..._lacneuchatel`, ...), not per tile. Each has an
  `*.esriasciigrid.zip` (one ESRI ASCII grid per km, `swissBATHY3D_CHLV95_LN02_E_N.asc`, 500x500
  cells of 2 m for Léman, cell-corner header, `nodata_value -9999`) and an `*.xyz.zip`. LV95 / LN02,
  the bed's elevation, same height system as swissALTI3D. Léman's grid zip is 409 MB (693 km).
- Download: `python tools/swiss_data.py swissbathy3d --bbox E0 N0 E1 N1` (whole lakes touching the
  box) into `<data>/bathy3d/`. MapSetup passes `--bathy <data>/bathy3d` when that folder exists.
- Coverage: Léman's Swiss part is surveyed to the shore; its French part only below ~5 m (the
  survey stops on the ~4 m isobath off Hermance/Chens). The grid also reaches **up the streams**
  around Nyon, 2-10 m above the lake: only used in a body >= 0.2 km² within 1 m of its mean level.
- **Spikes**: single cells 8 m below their neighbours on the survey's edge (2510047,1132676 at
  359.9 m among 368.0 m). `BathySource.Despike` drops a cell > 3 m from the median of its surveyed
  8-neighbours.

## Format: `water_E_N.water` (`tools/TerrainFormat/WaterFormat.cs`)

- 20-byte `TileHeader`: magic `USWL`, version 1, flags 1 (deflate), count = 1001 (cell side), as
  `.cover`. Then one deflate stream: 1001² u16 levels (little-endian), then 1001² fetch bytes.
- **Level** = `ChunkFormat.Quantize` steps (7.2 cm, the heights' own), so a level is bit-identical
  to the flat swissALTI3D surface it came from; **0 = dry**. A lake is flat (Léman: 99.8% of its
  vertices at 372.14), a river keeps its downstream gradient.
- **Fetch** = min(√(body area), local width) in 20 m steps (255 = 5.1 km+), for the waves.
- Only tiles with water get a file. Shared edge vertices hold the same values in both tiles.
- `WaterGrid.ToTile()` gives #299's `WaterTile` (2 m lattice, float level NaN dry, fetch in m);
  `LocalChunkSource`, `CachingChunkSource` (slot `Water`), `NetworkChunkSource` (`AssetKind.Water`
  = 11, streamed like `.cover`; an older server answers "missing") serve it.
- Golden: `TileHeaderGoldenTests.Water_header_bytes_unchanged_and_payload_inflates_back`.

## The pass: `WaterStage` (`--water`, and after every `--cover`)

1. Tiles with Water cover (or an old `.water`): surface = old `.water` level where there is one,
   else the terrain. **Re-runnable**: a second run recomputes from the original level instead of
   digging the bed deeper; a vertex wet before and dry now gets its level back as terrain. The
   `.water` is written before the `.terr`, so an interrupted run repeats safely.
2. Water bodies across the region: 4-connected flood fill per tile, union-find along the shared
   edges. Body area -> max depth (`WaterBed.MaxDepthForArea`).
3. Each tile in a window of itself + **500 m** of its neighbours (3x3 tiles), every per-vertex value
   read from the window with a canonical tile order, so a vertex two tiles share gets the same
   inputs in both:
   - distance to shore = exact Euclidean distance transform (`DistanceTransform`, Felzenszwalb) to
     the nearest known dry vertex, capped at 500 m (missing tiles are neither land nor water: the
     lake does not end at the region's edge);
   - local half width = climb that distance field to the ridge in the middle (infinite past 70 m);
   - survey depth = level - `BathySource.SampleBed` (bilinear between cell centres, valid only if
     every weighted cell is surveyed);
   - **gap fill** (water, no survey, within 400 m of it): `depth = Dedge · ds / (ds + dv)` (ds to
     the shore, dv to the survey), blended into the synthetic profile from 200 to 400 m. Dedge is
     the survey's depth at the nearest surveyed vertex, **smoothed by two 40 m box passes**: the
     nearest vertex alone jumps on Voronoi edges and drew 1.4 m steps across the French shallows.
     The box sums are exact integers (0.1 mm): a float summed-area table rounds differently from
     each window's origin and broke the seams;
   - synthetic elsewhere: `WaterBed.Depth(ds, halfWidth, maxDepth)`;
   - every depth <= ds (1:1): a quay in the survey becomes a 45° bank, and the bed meets the shore.
4. Writes `.water`, `.terr`, `.terrc`, the manifest's min/max, `horizon.bin`, then
   `TerrainBuild.VerifySeams` on every touched tile and its neighbours (exit 1 on a seam error).

## Synthetic bed: `WaterBed` (`tools/TerrainFormat/WaterBed.cs`, shared)

- Open water: shelf 1:12 out to 24 m (2 m deep), then drop-off 1:4 down to the body's max
  (0.3·√A^0.6, 1.5..150 m: a 50 m pond 3 m, 500 m 12 m, 10 km² 38 m).
- Channel (width < 80 m): parabola across the width, centre depth 0.4 m + 4.5 cm per m of width
  (Rhône's 50 m: 2.65 m), never deeper than open water at the same distance; blends to open water
  between 80 and 120 m wide, so a river widening into its lake has no step.
- "Rivers get a channel profile scaled by the TLM width": the width is the TLM water polygon's own
  (cover raster, distance field). TLM `fliessgewaesser` lines carry no width attribute, and a line
  narrow enough to be missing from the raster gets no bed (it stays a draped `WaterMeshBuilder`
  ribbon, as before).
- **Generated world** (`ProceduralWorld`): lakes `Level - WaterBed.Depth((Lake - 0.5) · 500 m, ∞,
  lake max)` (the weight runs 0.5 -> 1 over ~250 m from the shore; max depth from the relief lake's
  node area), rivers carve their channel profile below the flat bottom, which stays the water level.
  `BuildWater` gives the level where the cover says Water and the ground does not stand above it.
- **Fixture**: the `lake` course is #299's (`Fixture/FixtureLake.cs`, its own shelf and drop-off).

## Checks and numbers (western region, Léman, 152 water tiles of 316)

- `--water --bathy DIR --dump-png DIR`: per-body table (area, level, max/mean depth, % surveyed,
  % gap fill, published max), the survey/synthetic join (bed step between neighbouring vertices:
  median, p99, max and where), seam verify; `water_shade_*` (hillshade x3 at 2 m/px, a seam is a
  line) and `water_depth_*` per cluster; `--png-crop E,N[,size]` adds 1 m/px crops.
- Petit Lac (in region): max **78.2 m** (published ~76 m), mean 44.4 m, 97.8% surveyed, 2.2% gap
  fill; join median 0.15 m, p99 1.06 m (the 1:1 bank), max 1.76 m. 132 bodies, 55 km² of water.
  Rhône at Martigny (synthetic channel): max 3.4 m. 21-36 s for the region with 8 jobs.
- `tools/BlendCheck` "water:" section: generated Léman 58.5 m deep, no wet sample whose bed is above
  its level, shore samples <= 1.5 m deep, water levels equal across tile edges.
- Unit: `WaterBedTests` (profiles, blend without a step, distance transform vs brute force),
  `TerrainFormatTests.Water_*`, the golden.

## Gotchas

- The French side of Léman has **no swissALTI3D**: land there is height 0 (a 372 m cliff at the
  shore, before and after #298). Not a bed artefact.
- A client's tile cache is keyed by file name: a server that swapped in bedded tiles would keep
  serving flat lakes from it. `ClientTerrainSync.DropChangedTiles` deletes cached `.terr/.terrc/
  .water` of every tile whose manifest min/max changed since the last cached server index.
- Anything that took the terrain height at a Water cell as the water surface now reads
  `ChunkManager.TryGetWaterLevel` (Ambience's lapping, Gathering's water, `BirdLife.Ground`).
- Re-running roads (`--roads-only`, RoadGen) after the pass drapes on the bed; bridges keep their
  surveyed Z, and no road should cross water otherwise.
- Memory: every water tile's wet mask + surface stay in memory (3 MB each) for the region-wide
  labelling; a 16-thread run of the western region peaks around 3 GB.

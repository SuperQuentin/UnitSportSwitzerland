# RoadGen

- **RoadGen** (`tools/RoadGen/`, standalone, no Godot): a lab for road *geometry*. Builds a
  road network graph (endpoint snapping, X-crossing noding, T splitting, all layer-aware),
  fits **clothoid** spiral-arc-spiral corners so curvature never jumps, then makes junctions
  **explicit polygons** that the roads stop at instead of overlapping. Markings are offset
  curves generated only between the junction trims. Exports SVG plan views and OBJ, and
  self-checks (seam gap, chord budget, endpoint drift, degenerate triangles) with a non-zero
  exit on failure. `--demo` runs four hand-built scenes with no data at all; `--tiles` reads
  real `.road` files; `--synth` grows a network from a tensor field. `--rewrite` is the
  **road network stage** (#115): `TerrainPreprocessor` runs it after the road stage, reading the
  raw extractor output kept in `<chunks>_temp/roads_raw/` (+ `.keys`: TLM uuid, part, along-line
  metre per segment, for the OSM overlay) and writing `.road` v3 (`road-format-v3`). Always from
  the raw input, so a rebuild gives byte-identical tiles (checked, 513 tiles, twice, and
  `--rewrite` alone gives the same bytes). A rewritten tile (v2, or v3 flagged Network) with no
  raw input is refused (exit 3; `--skip-rewritten` leaves it, `--force` trims it twice). A fresh
  v1 tile is stashed into the raw dir first. The stage also stores each divided carriageway's
  direction (LaneGraph's partner rule, exact 3..30 m band instead of 30 m cells: 97.5 % get one
  vs ~70 % at runtime) and applies the OSM overlay; it prints region stats (one-way, urban, OSM
  km, bytes per tile raw and deflated). `--compare-v2 V2DIR --chunks V3DIR` checks a v3 region
  against a v2 build: geometry, one-way agreement with the runtime inference, bytes.
  Old in-place `--rewrite` read neighbours it had already trimmed as halo context: 16 of 513
  Martigny-Sion tiles, all on 6 km block edges, differ from v2 in 1-4 segments for that reason.
  `--tiles` on real data fails its own 50 mm chord budget (99-114 mm) on v2 and raw input alike.

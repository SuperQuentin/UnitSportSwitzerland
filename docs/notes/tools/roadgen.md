# RoadGen

- **RoadGen** (`tools/RoadGen/`, standalone, no Godot): a lab for road *geometry*. Builds a
  road network graph (endpoint snapping, X-crossing noding, T splitting, all layer-aware),
  fits **clothoid** spiral-arc-spiral corners so curvature never jumps, then makes junctions
  **explicit polygons** that the roads stop at instead of overlapping. Markings are offset
  curves generated only between the junction trims. Exports SVG plan views and OBJ, and
  self-checks (seam gap, chord budget, endpoint drift, degenerate triangles) with a non-zero
  exit on failure. `--demo` runs four hand-built scenes with no data at all; `--tiles` reads
  real `.road` files; `--synth` grows a network from a tensor field. Not wired into
  `TerrainPreprocessor`; `--rewrite` post-processes built `.road` tiles in place instead, which
  is what actually gets junctions into the game — see its README for why that seam and what is
  missing.

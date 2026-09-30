# Data pipeline

- **Data pipeline**: swissALTI3D XYZ zips (`ressources/data/swiss_chunks/`, LV95/EPSG:2056,
  0.5 m grid, 1 km tiles) → `tools/TerrainPreprocessor` → `.terr` binary chunks +
  `manifest.json` in `terrain_chunks/` (**1001×1001 vertices, 1 m grid** — format `Version = 2`,
  global uint16 quantization so tile seams are bit-identical). Runtime never parses XYZ.
  1 m was chosen over matching the source's 0.5 m exactly: it still recovers real detail the old
  2 m grid discarded, at 4× the vertex count per tile rather than 0.5 m's 16×, and `GridSize-1`
  (1000) still divides every LOD/coarse stride (1, 2, 4, 10, 20) cleanly, so the ring table needed
  no redesign. **The source-cell averaging (`TileReducer`) is ratio-general** (`Ratio =
  XyzParser.CellsPerSide / (GridSize-1)`, currently 2): it used to hardcode a 4-cell window
  regardless of ratio, which was only correct by coincidence at ratio 2 and silently discarded 12
  of the 16 cells a 2 m vertex's true footprint covered at the old ratio 4 — real, measurable
  aliasing, confirmed by rebuilding the same tiles both ways and comparing (field boundaries and
  mountain rock texture visibly sharper after the fix; a furrow micro-pattern in flat fields was
  entirely smoothed away before it). **Cover-class boundaries are blended, not hard-edged**:
  `TerrainMeshBuilder.BoundaryBlendedColor` averages a vertex's colour with any of its four
  cardinal neighbours that hold a different class, so a forest/meadow edge is a gradient across
  one quad instead of an instant jump — worse before this at coarse LOD strides, where a single
  20-40 m quad could straddle the whole boundary. A region built before `Version = 2` has 2 m
  tiles and must be fully re-preprocessed (`--verify`, then `--coarse`, then `RoadGen --rewrite`)
  before its coarse companions and junctions are valid again.

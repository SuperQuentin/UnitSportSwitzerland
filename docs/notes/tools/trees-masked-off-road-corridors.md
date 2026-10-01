# Trees must be masked off road corridors

- **Trees must be masked off road corridors.** TLM forest polygons cover the whole wood
  including the road cut through it, so scattered trees grow in the carriageway and
  completely hide tunnel portals. `CoverStage.BuildRoadMask` stamps corridors (wider at
  tunnels/bridges) before the scatter. Symptom to recognise: a screenshot that looks like
  "camera inside terrain" is often camera inside a tree canopy.
- **Mask = raw lines OR final lines** (#114 integration): the raw extractor lines
  (`<chunks>_temp/roads_raw`, they run through the junction areas the network stage trims) plus
  the network stage's `.road` tile, whose motorway carriageways #117 moved ~4.8 m outward and
  widened to 10.5 m. So `TerrainPreprocessor` now runs roads for every batch, then the network
  stage, then cover and buildings per batch (it used to run cover before the network stage, and
  trees stood on the shifted A9). A `--cover-only` rerun reads the same two files: same trees.

# Water

- **Water**: built at runtime from the Water cover class, not a separate file —
  swissALTI3D already models lakes/rivers as flat surfaces at water level, so the terrain
  height at a water cell *is* the water level, and rivers keep their downstream gradient
  for free (`WaterMeshBuilder`, +0.12 m lift, `ps1_water.gdshader`).
- **Since #298** that holds only for tiles built before it: the preprocessor's `--water` pass puts
  the **bed** in the heights and the level in a `.water` file (`tools/bathymetry`); the runtime
  reads the level through `ChunkManager.TryGetWaterLevel` (`water-level-layer`, #299), never the
  terrain height.

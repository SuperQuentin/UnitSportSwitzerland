# Water

- **Water**: built at runtime from the Water cover class, not a separate file —
  swissALTI3D already models lakes/rivers as flat surfaces at water level, so the terrain
  height at a water cell *is* the water level, and rivers keep their downstream gradient
  for free (`WaterMeshBuilder`, +0.12 m lift, `ps1_water.gdshader`).

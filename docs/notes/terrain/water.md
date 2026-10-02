# Water

- **The surface is its own mesh, laid on the tile's still water layer** (`WaterMeshBuilder` from
  `WaterLayer`, #299; the layer's shape and sources: `water-level-layer`). A quad where all four
  corners of a 2 m lattice square are wet, vertices at the still level, the wave scale in UV.x;
  every 2 m sample where the terrain is drawn at stride 1-2 (round the camera), every 4 m further
  out. No collision: the terrain's collision is the bed. Bounds grown by 3 m
  (`WaterMeshBuilder.WaveMargin`) for the crests the shader raises.
- **Legacy tiles** (no water layer from the source): the layer comes from the cover raster, as the
  surface always did — swissALTI3D models lakes and rivers as flat surfaces at water level, so the
  terrain height at a water cell *is* the water level (+0.12 m lift), and rivers keep their
  downstream gradient for free. 0.12 m deep: no waves, cars drive on it.
- **Mapped watercourses** (`.road` lines) are still ribboned on their draped heights, no waves; a
  line mostly inside the layer's water is skipped (the layer draws that river).
- **Shading**: `shaders/body/water.gdshaderinc` (+ `ps1_water.gdshader`): displaced in `vertex()` by
  `shaders/common/waves.gdshaderinc` (`docs/notes/world/water-field.md`); PS1 draws it translucent,
  clearer toward the shore by the water depth behind each pixel (depth texture), stepped and
  dithered alpha, and a bright banded underside from below.
- The fixture `lake` course (`Terrain/Fixture/FixtureLake.cs`) has a real bed: beach, 150 m shelf
  to 2.5 m, a drop-off to 25 m, a river; its surface runs on under the beach (wet up to 1 m of
  ground above the level) so the terrain hides its edge.

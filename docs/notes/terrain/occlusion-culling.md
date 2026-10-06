# Occlusion culling: the buildings as occluders (#553)

- **Why cells.** Godot culls whole instances, and a tile's buildings are one mesh a kilometre
  wide: no building ever hides a tile, so occluders alone hide nothing of a city. Round the camera
  (`ChunkManager.CellRings`, 1: the 3×3 tiles) a tile's buildings are cut into 125 m cells
  (`BuildingOcclusion.SplitByCell`, by triangle centroid, on the build worker), each its own
  `MeshInstance3D`, so a block behind a row of houses is culled when the row hides it.
- **The occluders** (`BuildingOcclusion.Occluders`): every building whose roofs cover at least
  `MinFill` (85 %) of its plan box (`BuildingTypes.Boxes`) is a box from its floor to its eave,
  `Inset` 0.5 m inside its walls (never in front of a car or a sign against the wall, clear of the
  PS1 snap). An L or a U is left out: its box would hide its own courtyard. One `ArrayOccluder3D`
  per tile, built with the cells.
- **Only near the ground.** Cells are wanted while some anchor is under `StreetEnterM` (40 m)
  above the ground, and given up once every anchor is over `StreetLeaveM` (120 m). From the air a
  building hides next to nothing, and a flight rebuilt three tiles at every crossing for no gain.
- **Switching costs the buildings only**: a tile crossing into or out of the cell rings rebuilds
  its building mesh (`buildingsOnly`), not its trees and water. `ChunkState.BuildingCells` records
  what was *asked for*: recording what came back made a tile with no buildings (the lake) want
  cells for ever and rebuild every evaluation.
- **Off where it would hide the wrong thing** (`ChunkManager.ApplyOcclusion`): in VR (two eyes,
  one occlusion buffer) and while a sightline cut dissolves the buildings between a camera and its
  subject. The door portals' `SubViewport`s never cull: their cameras stand inside the walls. A free
  camera flown *inside* a building sees nothing past it: the occluder is around it.
- **On by default; `--occlusion off` turns it off** (`ChunkManager.OcclusionCells`). Measured three
  runs each way on the routes of `perf-static-city`:

  | | street off | street on | aerial off | aerial on |
  |---|---|---|---|---|
  | frame p50 | 4.3–4.4 ms | 4.0–4.1 ms | 5.2–5.3 ms | 5.0 ms |
  | frame p99 | 5.6 ms | 5.0–5.1 ms | 8.3–8.4 ms | 8.4–9.1 ms |
  | mean frame | 4.45–4.52 ms | 4.12–4.18 ms | 5.34–5.45 ms | 5.13–5.22 ms |
  | GPU | 4.22–4.28 ms | 3.88–3.92 ms | 4.96–5.01 ms | 4.70–4.75 ms |

  The cells cost about 270 more draws round the camera (render-thread CPU +0.2 ms), which the
  culling more than pays back. Before the lake fix and the street-level gate, the same flight did
  1 007 builds instead of 731 and doubled its gen2 collections: watch `builds finished` in
  `summary.txt` when changing either.
- **Checks**: the same view with and without (`--shot`, then `--shot ... --occlusion off`) must be the same picture;
  with the occluders in place a street-level view facing a wall drew 15.9 M primitives instead of
  32.8 M, and the two frames differed by a 21×4 pixel speck.

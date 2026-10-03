# A ground rebuild must load the road tile even when the roads are already drawn

- **A collision build must load the road tile even when the roads are already drawn.** Both the
  road-blended floor and the bridge-deck collision are built in `StartBuild`'s tail from the road
  tile, but `EvaluateRings` only asked for roads when they were *missing*. The ordinary way to
  play — fly over an area (roads load, no collision: the fly camera does not ask for it), then
  drop on foot — therefore built every tile's collision from BARE terrain and no bridge collision
  at all: the player stood the full `DrapeOffset` (0.35 m + class lift) inside every road and
  path, and fell straight through every bridge. `StartBuild(..., roadsForBlend:)` now fetches
  the (cached) road tile for the blend without re-meshing the roads. Measured with
  `--roadcheck`: floor − ribbon went from −0.42 m mean to ±0.005 m.
- **The same goes for a near-field mesh rebuild** (stride <= `TerrainMeshBuilder.MaxHoleStride`):
  the visual mesh is lowered `VisualBlendClearance` under the roads in the tail, from the road
  tile. Flying a few km away coarsens a tile while its roads stay drawn (out to `RoadMaxDist`);
  coming back refined it with no road tile, so the stride-1 ground came back unblended and
  swallowed the roads and railways that looked fine at spawn. `roadsForBlend` is now
  `(needCollision || near-field needMesh) && want.Roads`.
- **And a refined mesh waits for its blend when ground is already on screen.** The surface goes
  out as an interim result before the road tile is read, which put the bare stride-1 ground over
  the roads for ~0.5 s on every return. When the tile already shows a coarser mesh and this one
  will be road-blended (near field), the interim surface is held and goes out with the tail; the
  commit then leaves `ActiveStride` alone on a mesh-less interim, so a cancelled tail still
  rebuilds. Repro: `--origin 2507700,1137300 --shot-queue` with Nyon, Crassier (-5400,g300,1900),
  Nyon again.

# Roads are merged into terrain collision, not just draped over it

- **Roads are merged into terrain collision, not just draped over it.** The player always
  physically stood on the bare-terrain `HeightMapShape3D` — roads had no collision of their own
  at all — which was invisible on flat ground but a real mismatch wherever a road's surveyed
  height genuinely diverges from raw terrain (an embankment, a cut, a graded approach
  swissALTI3D never modelled) and **severe** on a bridge: zero collision under the deck, so a
  player walking onto a visual span fell straight through to the valley floor below.
  `TerrainMeshBuilder.ComputeRoadBlend` closes the ordinary case: for every at-grade
  road/path/rail segment (excluding `RoadFlags.Bridge`/`Tunnel`, aerial ropeways, watercourses
  and walls — none of those is a ground-level surface), it walks the segment's own densified
  polyline and smoothsteps the terrain **collision** floor toward the segment's own stored
  height — already carrying `RoadExtractor`'s approach-ramp blend from preprocess time, so no
  height is re-derived — from full weight at the road's own half-width out to zero
  `CorridorFalloffM` (3 m) beyond it — stamped every 1 m, so the pull **compounds** and a
  shoulder ends much nearer the road than one smoothstep says. That compounded shape is what the
  game has, so it is kept: the recursion is linear in ground height, so each cell is carried as
  `ground·P + S` and ONE sparse pass (`RoadBlend`) serves collision (clearance 0) and the visual
  mesh (`VisualBlendClearance`), with the stamp weights tabled per road. The visual tail is
  `PatchSurface` on the interim mesh's core — only corridor vertices move — not a second
  million-vertex build; verified bit-identical to a rebuild, and heights within 1 mm of the old
  blend. blend+tail went 17.8 -> 6.7 ms/tile (488 -> 194 ms at stride 1). A distance-field blend
  (true single smoothstep) was tried and is ~17 cm different on average, up to 55 m on cliffs:
  it is a look change, not an optimisation. Bridges are excluded on purpose: a heightfield has one
  height per (x, z) column, so it cannot represent a deck floating above the gorge it crosses —
  blending toward deck height there would fill the gorge in. Those get `RoadMeshBuilder.
  BuildBridgeCollisionFaces` instead, a small `ConcavePolygonShape3D` for the deck TOP only
  (mirroring `BuildingMeshBuilder.BuildCollisionFaces`'s pattern) — piers and parapets stay
  visual-only, since falling through the deck was the actual reported problem, not clipping a
  pier. Both run in `ChunkManager.StartBuild`'s **tail**, after the road tile has loaded: the
  bare-terrain collision still publishes immediately in the **interim** result so the ground
  never waits on roads (see the interim/tail split below), and gets silently replaced with the
  blended version once available — `CommitReadyResults` already re-applies `SetCollision`
  whenever a later `BuildResult` carries a non-null `CollisionMap`, so no new commit path was
  needed, only a second call to `BuildCollisionMap` with the road tile it didn't have the first
  time. Tunnel interiors are not touched here — the existing hole-carving at the portal already
  works, mostly by the coincidence that undisturbed rock blocks a player; verify with `--probe`
  before assuming that needs its own collision too.

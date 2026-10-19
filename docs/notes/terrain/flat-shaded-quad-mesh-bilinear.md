# A flat-shaded quad mesh is NOT a bilinear surface, and a height query must match whichever one is actually on screen

- **A flat-shaded quad mesh is NOT a bilinear surface, and a height query must match whichever one
  is actually on screen.** `ChunkGrid.SampleHeight` blends all four corners of a quad smoothly;
  `TerrainMeshBuilder.BuildSurface` splits every quad into two FLAT triangles along a fixed
  diagonal. At full resolution (2 m spacing) the two agree to a few centimetres and nobody
  notices. At the coarse LOD strides most of a streamed world renders at beyond ring 4 (20-40 m
  spacing), they diverge by up to **1.4 m on real terrain here** (measured: stride-10 tile,
  400 random samples, max 1.44 m, mean 5 cm) - enough that a GPX ribbon's 0.28 m tread lift was
  nowhere near enough to clear it, and the route visibly sank under the ground the player could
  see. `ChunkGrid.SampleMeshHeight` replicates the mesh's own triangle split exactly (verified
  continuous across the diagonal), and `ChunkManager.TryGetHeight` - the avatar, the GPX ribbon,
  the cinema camera's ground and `CanSee` checks, all of it - now goes through that instead.
  `SampleHeight` itself is untouched: the preprocessor and `RoadMeshBuilder` call it against
  always-full-resolution grids (`RequireFull()`-guarded), and their baked output was generated
  against it, so changing it would need a full re-preprocess of already-built terrain for no gain.

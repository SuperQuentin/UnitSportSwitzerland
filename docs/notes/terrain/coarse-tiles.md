# Coarse tiles

- **Coarse tiles**: every `.terr` has a `.terrc` companion — the same grid **point-decimated at
  stride 10** (51x51, **5.2 KB** against 490 KB). The LOD rings render one vertex in ten or twenty
  past ring 4, so 280 of the 361 tiles an anchor wants were reading a 490 KB file to use 5 KB of
  it. Decimation, not averaging, is what makes it free: `TerrainMeshBuilder.BuildSurface` already
  samples `HeightMetersAt(c * stride, r * stride)`, so the kept vertices are *exactly* the ones
  the mesh uses and the geometry is bit-identical (the `--coarse` pass asserts this per tile at
  both strides). A tile reads the full grid whenever anything is built **onto** it — collision,
  roads, watercourses, building footings all sample the heightfield — so the rule is full at
  d <= `RoadMaxDist`, coarse beyond. `ChunkGrid` carries its own `Stride` and `RequireFull()`
  guards the callers that cannot take a 20 m lattice. The whole region's companions are 33 MB.

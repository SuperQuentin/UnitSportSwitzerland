# Cliffs: lit by a smoothed normal, quads split along their shorter diagonal (#883)

- **Symptom:** every rock wall (Dents du Midi, Diablerets, the Rhône walls) drew as vertical
  "organ pipe" stripes in all styles: a comb of light and dark slivers, not a wall in the sun.
- **Cause 1, the big one:** the terrain body shades each fragment by its facet (`facet_normal`).
  On a cliff the 1-2 m facets are tall slivers, each tilted by the height data's noise, so each is
  lit differently; Cartoon's terminator snaps them to lit/shaded, PS1 gives rock 3 light steps,
  Realistic's triplanar rock switched projection sliver by sliver.
- **Fix 1:** `TerrainMeshBuilder.MeshData.Normals`, per vertex, smoothed over ±3 m (`NormalReachM`)
  of the drawn heights (`SmoothNormal`: a box of slopes on three rows and columns, one-sided at the
  tile's edges); `PatchSurface` redoes the normals whose box reaches a road-moved vertex. The body
  blends facet → smoothed as the smoothed normal's y falls from 0.8 to 0.55: gentle ground keeps its
  low-poly facets, walls light as walls, and rockiness, snow and the rock projection follow.
  Straight-up normals mean "keep the facet": cut walls, horizon blocks, and any mesh on the terrain
  material must carry them (a mesh with no normal array reads (0,0,1) as a south-facing cliff;
  `PortalDemo`'s ground sets `Vector3.Up`).
- **Cause 2:** one fixed diagonal (v10-v01) for every quad: on a wall not aligned with it the two
  triangles face apart, a fine zig-zag. **Fix 2:** `ChunkGrid.SplitsNwSe` picks the diagonal that
  climbs less, from the grid's own heights (not the road-blended ones, so `PatchSurface` can keep the
  core's indices); `SampleMeshHeight` decides there too, so height queries still match the screen.
  Alone it removes the fine comb, not the streaks.
- **Check:** `--origin 2563500,1112500 --shot-queue` with `0,2500,-1600,-12,180,25,out.png` (the
  north face) in `--style ps1|cartoon|real-`; compare with a `main` build.

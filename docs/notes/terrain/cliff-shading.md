# Cliffs: lit by a smoothed normal (#883)

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
- **Not done: a per-quad diagonal.** Splitting each quad along its shorter diagonal took out a fine
  zig-zag on walls, but Jolt's height field (the ground collision) splits every cell along the same
  fixed diagonal as the mesh always did (v10-v01: a headless ray at the centre of a quad with its NE
  corner raised reads half its height). Flipping only the drawn one put the ground you see and the
  ground you stand on apart on every flipped quad, so the mesh keeps the fixed split.
- **Walking on that ground** is `player/steep-ground`.
- **Check:** `--origin 2563500,1112500 --shot-queue` with `0,2500,-1600,-12,180,25,out.png` (the
  north face) in `--style ps1|cartoon|real-`; compare with a `main` build.

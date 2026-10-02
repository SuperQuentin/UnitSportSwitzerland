# Sidewalks and tunnels at runtime (#119)

- **Sidewalks** (`RoadStreetBuilder`): per side with `SidewalkDm`, a slab offset like the ribbon
  edges, top at road + `KerbCm`, vertical kerb face, outer skirt 0.6 m down, end faces where the next
  piece does not carry on; vertex chains thinned (1.5 cm plan / 1 cm height). Corner patches (APRP
  `Sidewalk`): top + faces on the outline. Collision in the two-sided road body: top + kerb
  **chamfered 45°** (a 0.32 m foot capsule meets a vertical 12 cm step at 51°, its floor limit is
  52°); corners vertical.
- **Bike paths (#120)**: a side is drawn from its `RoadStreetSection` profile (shared with the
  network stage): verge / path / buffer / sidewalk bands, each piece coloured (grass, darker
  asphalt for the path), vertical kerbs and the sloped 0.30 m kerbs beside a path; the collision
  is the same profile with every vertical step chamfered 45°. The blend raises to the side's outer
  height (`OuterHeight`), so a mid-level path lies on the slab, not on the ground. Bike symbols:
  `RoadPaintBuilder` expands `RoadPaintGeometry.BikeSymbol`. Note `bike-infrastructure`.
- **Road blend** (`TerrainMeshBuilder.RoadBlend.cs`): under a slab the ground stays at road height
  (raising it let the lattice poke through on climbing bends); that side's slopes start 8 cm under
  the sidewalk top; round a line's *real* ends (not straight cuts between pieces, `CarriedOn`) only
  the carriageway is level and no disc is stamped; junction caps are road surface; corner patches
  and caps clamp the ground to their own height; the 0.35 m visual clearance fades out over 1.5 m of
  slope (roads at ground level would sit in a trench). Ground over every bore ≥ crown + 0.3 m (last,
  so no wall frees it).
- **Road shader**: whole road mesh pulled 0.04 % of its distance toward the eye, plus up to 2 m past
  800 m (roads at the ground vs decimated terrain). Ribbon ends run on 15 cm, 1 cm lower, under the
  cap or next piece (the cap/arm hairline showed the ground).
- **Tunnels**: clear height `RoadTunnels.ClearHeight` = class height, lowered only under a road
  crossing over it, ≥ 3.2 m (it used to follow the raw terrain cover: a 2.4 m bore under a city).
  The `.holes` punch is only at a **mouth** (`TunnelCarver`): an end that meets a road/rail or
  nothing, where the bore meets the surface (< 3 m of ground over the crown: TLM cuts underground
  lines at every station hall), half a quad in front of it to 1.5 m in, bore width + 0.3 m. Runtime
  mouths (`ComputeTunnelPortals`) also skip ends carried on by another piece and tile-seam ends.
  Portal block: arch face 1 m out + 3 m concrete top over the punch; no lining walls along the hole.
  Bore mesh has a floor. **Collision**: floor (bore width + 0.6 m, out through the mouth) and walls in
  the road body.
- **Safety nets** (`FootPlayer.RescueFromVoid`, `VehicleBody`) compare against the *raw* terrain;
  they now skip a body inside a bore (`ChunkManager.InTunnel`, bores kept per tile with its
  collision) or with any floor within 4 m below (`ChunkManager.FloorBelow`: ramps the blend cut 4 m
  down). Vehicle exits in a tunnel stay in the bore.
- **Checks**: `--roadcheck --sidewalks [--at E,N]` (slab tops, by the kerb, past the sidewalk, corner
  floors; use `--traffic 0`, NPC cars push probe bodies); `--roadcheck --floorat E,N` (blend height at
  the 4 lattice vertices); `--probe E,N,s` counts a hole open when its ray meets nothing or the road
  body; `--roadperf DIR[,label]` (headless per-tile blend/mesh/paint/collision cost of a region's
  files, CSV in `test_output/`).

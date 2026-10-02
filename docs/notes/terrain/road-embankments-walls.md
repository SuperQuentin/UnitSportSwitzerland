# Road embankments and retaining walls (#125)

- **Shape**: every at-grade road, path and rail line is level across at its centreline height out
  to each side's edge (`RoadEmbankment.EdgeOffset`: the wider of the drawn width and v3 `widthCm`,
  plus that side's sidewalk and verge from #119). Past the edge the ground is clamped between a 2:3
  fill slope below and a 1:1 cut slope above, out to 7 m (3 m for tracks, paths, rail, squares).
  Ground already inside that band does not move, so a road on gentle ground changes nothing beside
  it. All numbers live in `tools/TerrainFormat/RoadEmbankment.cs`, shared by build and runtime.
- **Blend** (`TerrainMeshBuilder.RoadBlend.cs`, Godot-free so BlendCheck compiles it): a cell holds
  a band `[Lo, Hi]` and the ground is clamped into it, one sparse pass for collision (clearance 0)
  and the visual mesh (0.35 m). Under a road `Lo = Hi` = the centreline at the cell's foot, nearest
  segment wins. Each cell keeps the tightest bound any road gives (an envelope, so draw order does
  not matter); a slope never reaches under another road; fill-above-cut conflicts split the
  difference. Each piece is rasterised as one strip, row by row, with the cell logic inline (a Debug
  build does not inline a call); discs only at line ends and sharp bends.
- **Walls** are decided at build time by `tools/RoadGen/Network/EmbankmentPlanner.cs` in the network
  stage: every 2 m along a drivable at-grade road (Motorway..Lane) or railway line (never tracks,
  paths, fords, stairs, trams, funiculars, or rail embedded in a carriageway), each side, a wall where the slope misses the ground at its 7 m reach by more than
  0.5 m, or runs into a lower road or stream first (only the upper road builds between two stacked
  roads). Runs: one-sample gaps closed, < 6 m dropped, broken wherever the solid would stand on
  another line (a junction arm, the other leg of a hairpin). A surveyed TLM `Wall` at the edge wins:
  the prop is kept as variant 1 (frees the ground, not drawn). Written to the v3 `LPRP` section
  (`road-format-v3`). Each run is simplified to the points a straight chord cannot carry (plan
  within 5 cm, road-side height within 3 cm): 527 of 920 samples kept on the 6-tile test region.
- **Swiss dimensions** (Kanton Bern TBA, "Arbeitshilfe Entwurf und Gestaltung von Stuetzmauern",
  2023, after ASTRA FHB K / VSS): a 40 cm crown (the stored `Thickness`), 50 cm clearance between
  the edge and the wall, asphalt up to it. Fill wall crown 6 cm over the road; cut wall crown 20 cm
  over a level backfill. Fill face 0.9 m out from the edge, cut face 0.5 m (a paved gutter).
- **A heightfield cannot hold a vertical step**: its one-cell transition is up to a lattice diagonal
  wide wherever the line falls. So the face side is kept clean (ground released, or levelled with
  the road, out to `FreeDepth` 1.5 m inside the solid) and the transition lies under a *cover*
  `CoverDepth` 3 m deep from the face, part of the wall's mesh and collision: paving under the
  road's edge behind a fill wall, the level backfill behind a cut wall. Past the face of a fill
  wall lies the raw valley floor, behind a cut wall's cover the raw hillside.
- **Runtime**: `RoadWallBuilder` draws face (sunk 1 m below its foot so it meets any lattice),
  crown, cover and a cut wall's gutter as flat quads in the road mesh, and puts the same surfaces
  (not a fill wall's buried cover back) into the bridge-deck collision body, which is two-sided
  (`concavepolygonshape3d-one-sided-collision-unless`).
  Coarse LOD rings just sample the blended heights; no wall logic there.
- **Region (Martigny-Sion, 513 tiles, the earlier 3 m solid walls)**: 2,201 fill walls (63.6 km), 732 cut walls (22.9 km),
  321k m² of face; peaks <2/4/6/10/15/25/more m: 331/1580/657/358/59/16/10 (the tallest, 58 m, are
  roads drawn on a cliff lip, `draped-centreline-cliff-lip-spike`). Planning 2.5-3.3 ms/tile; the
  stage stays byte-identical across reruns. No TLM wall sits at a wall's edge in this region (245
  TLM wall segments in all).
- **Cost** (BlendCheck `--roads`, 513 tiles, full JIT): blend 2.5 ms/tile vs 3.7 for the old
  smoothstep corridor, collision apply 0.53 vs 0.33 ms; first call on the first tiles (tier-0 JIT)
  5.0 vs 8.4 ms with `AggressiveOptimization` on the strip loop. In the Debug game under load (tile
  workers in parallel) the same blend measured 23 vs 14 ms per tile median (64 tiles): more cells
  (7 m reach) and more arithmetic per cell, which only the optimising JIT hides. Drops over 3 m per
  lattice edge left in the corridors: 16.0/km vs 25.2/km.
- **Thin walls on the 6-tile test region** (Riddes + above Sion): 33 fill (0.76 km), 27 cut
  (0.97 km), highest 11.0 m, no rail walls there; LPRP 1.5 KB/tile. Blend 5.5 ms/tile (7.9 with
  the 3 m walls), unhidden drops > 3 m unchanged (1,063: terrain cliffs), 227 inside a wall's cover
  instead of 29. Region numbers for 513 tiles not rebuilt yet.
- **Checks**: `dotnet run --project tools/BlendCheck -c Release -- --roads DIR [--tiles E_N,...]
  [--render E,N --scale 0.25] [--walls]` (timing, drops, wall stats, hillshade bare | blended with
  wall faces white, `--walls` lists every wall with a ready `--shot`); `<godot> --path . --
  --roadcheck --embankments [--at E,N]` (cap rest, fall past the face, floor at the wall foot and on
  slopes = the blend at lattice vertices to 5 cm). Multiplayer: `--walloff[,s]` on a connected
  client steps the local player off the nearest wall; `--netsmooth` prints `floor_gap_m`, the remote
  player's height over the collision under it, and the server log prints the walker's replicated
  height over the ground. A headless client joins on foot 4.8 km up while its tile streams and
  falls; `--walloff` teleports with `PlaceAt` (no fall charged) and waits out a knock-out, or the
  revive puts the body back on its last safe spot mid-check. A `--netsmooth` observer that is still
  falling loses a walker on the ground (out of view): read the server's log instead.
- **Known**: the fill wall at 2597616,1119839 (Sion) fails `--walloff` with both the 3 m and the
  thin walls: the body rests 0.25-0.6 m over the floor under it after a 0.8-1.3 m drop, on ground
  that rises right in front of the face (also visible as lattice triangles over the face). The
  same tile's `--roadcheck` fails on a Path/Natural sample 0.1 m under its ribbon, also on both.
- **#119**: tunnel approach ramps (within 60 m of a tunnel end) get a wall wherever the ground 3 m
  past the edge is > 1.2 m off the road (the terrain holds the trench, so the slope rule never
  fired), 1.5 m thick; no wall's solid or face stands on another line or a street's sidewalk.
  Blend changes for sidewalks, caps and bores: `sidewalks-tunnels-runtime`.
- **Not done**: steeper cut in rock cover, walls instead of slopes that would bury a building,
  slopes and walls from a road in the neighbouring tile (the blend sees one tile's segments, as before).

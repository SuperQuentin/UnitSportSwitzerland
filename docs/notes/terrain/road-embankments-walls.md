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
  stage: every 2 m along a drivable at-grade road (Motorway..Lane; never tracks, paths, rail,
  fords, stairs), each side, a wall where the slope misses the ground at its 7 m reach by more than
  0.5 m, or runs into a lower road or stream first (only the upper road builds between two stacked
  roads). Runs: one-sample gaps closed, < 6 m dropped, broken wherever the solid would stand on
  another line (a junction arm, the other leg of a hairpin). A surveyed TLM `Wall` at the edge wins:
  the prop is kept as variant 1 (frees the ground, not drawn). Written to the v3 `LPRP` section
  (`road-format-v3`).
- **A heightfield cannot hold a vertical step**, so the wall is a solid 3 m deep and the ground is
  released from every slope only past its *free line*, half the thickness inside the solid. The
  one-cell drop from shelf to released ground then lies inside the wall (worst case a lattice
  diagonal, 1.41 m, each side of the line). Fill wall: face 2 m out from the edge, back 1 m under
  the road (its cap is the shoulder, drawn 4 cm under the road against z-fighting). Cut wall: face
  0.2 m out, solid into the hill, top = highest ground across it. Behind a fill wall lies the raw
  valley floor, behind a cut wall the raw hillside.
- **Runtime**: `RoadWallBuilder` draws each wall as a closed prism (face sunk 1 m below its foot so
  it meets any lattice) into the road mesh, and puts the face and the cap (not the buried back) into
  the bridge-deck collision body, which is two-sided (`concavepolygonshape3d-one-sided-collision-unless`).
  Coarse LOD rings just sample the blended heights; no wall logic there.
- **Region (Martigny-Sion, 513 tiles)**: 2,201 fill walls (63.6 km), 732 cut walls (22.9 km),
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
- **Checks**: `dotnet run --project tools/BlendCheck -c Release -- --roads DIR [--tiles E_N,...]
  [--render E,N --scale 0.25] [--walls]` (timing, drops, wall stats, hillshade bare | blended with
  wall faces white, `--walls` lists every wall with a ready `--shot`); `<godot> --path . --
  --roadcheck --embankments [--at E,N]` (cap rest, fall past the face, floor at the wall foot and on
  slopes = the blend at lattice vertices to 5 cm). Multiplayer: `--walloff[,s]` on a connected
  client steps the local player off the nearest wall; `--netsmooth` prints `floor_gap_m`, the remote
  player's height over the collision under it.
- **#119**: tunnel approach ramps (within 60 m of a tunnel end) get a wall wherever the ground 3 m
  past the edge is > 1.2 m off the road (the terrain holds the trench, so the slope rule never
  fired), 1.5 m thick; no wall's solid or face stands on another line or a street's sidewalk.
  Blend changes for sidewalks, caps and bores: `sidewalks-tunnels-runtime`.
- **Not done**: steeper cut in rock cover, walls instead of slopes that would bury a building,
  slopes and walls from a road in the neighbouring tile (the blend sees one tile's segments, as before).

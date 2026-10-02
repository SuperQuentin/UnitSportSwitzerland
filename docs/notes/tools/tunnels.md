# Tunnels

- **Tunnels**: `kunstbaute` (Tunnel/Unterfuehrung/Galerie) segments keep their surveyed Z
  and get an extruded arch bore (`RoadMeshBuilder.AppendTunnelBore`). `TunnelCarver`
  writes a `.holes` file per affected tile listing terrain quads to omit, so portals are
  open; `TerrainMeshBuilder` skips those quads and writes NaN into the collision map —
  Jolt treats NaN heightfield cells as holes, so tunnels are enterable (verified with
  `--probe`). The bore is extruded 5 m PAST each end of the centreline and capped with a
  headwall (wall with the arch cut out) plus wing walls — carving alone leaves the ground
  mesh with raw edges and the bore floating in the gap, so the wall is what actually joins
  tunnel to terrain. `TunnelCarver` extends its carve by the same 5 m.
  The **sides of the cut are lined by `TerrainMeshBuilder.AppendCutWalls`**, generated from
  the hole mask and the same height grid the surface uses — geometry built from the road
  centreline can never meet a hole quantised to the 2 m lattice, which is why the earlier
  wing walls floated. Both the bore height and the headwall are **clamped to the cover that actually exists**
  (`MinCover` / `TerrainAbove`): `Unterfuehrung` under a rail embankment may have only 3 m
  over it, and a fixed-height bore would stand above the track it passes under.
- **Superseded by #119** (`sidewalks-tunnels-runtime`): the carve is a punch at real mouths only
  (no more carving wherever the ground falls inside the bore's span, which opened whole city
  tunnels as trenches), the bore runs 1 m past the end, no cut walls line the hole, the height comes
  from `RoadTunnels.ClearHeight` (not the raw cover), the ground over a bore is kept above its crown,
  and bores have floor and wall collision.

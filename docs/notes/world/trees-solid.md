# Trees are solid

- **Trees are solid** (`World/TreeColliders`, issue #14): no per-tile tree collision — 100k+ trees
  a forest tile. A **pool** of `StaticBody3D` + `CylinderShape3D` trunks is laid out only around
  `ChunkManager.CollisionAnchors` (the local `FootPlayer`, a moving `VehicleBody`), 45 m round the
  anchor and round a point 1.2 s ahead along its velocity (capped at 40 m), from the same `.trees`
  files in 10 m world cells. Cells are handed out / taken back as the anchor moves (a released body
  is `ProcessMode.Disabled`, which removes it from the space, and reused), at most 64 trunks placed
  a frame. Trunk radius = the drawn trunk (0.10 × crown radius, clamped 0.12–0.5 m), height = the
  whole tree; shrubs (kind 1) stay walk-through; no crown shape, so a plane only hits a treetop's
  axis. **Layer 2** (`TreeColliders.Layer`): `FootPlayer`/`VehicleBody` add it to their mask, the
  camera pull-in rays (`FootPlayer.CameraMask`) leave it out so a chase camera is never shoved in by
  a trunk; combat rays use the default all-layers mask and hit trunks. Measured at the Col du
  Mollendruz (51% forest): 146–330 trunks live, **0.86 ms/frame max while riding**, ~3.8 ms the
  frame the pool first grows its bodies. Check: `<godot> --path . -- --treecheck[,out.png]
  [--at E,N]` rides a bike at a real trunk 25 m away; non-zero exit if it gets through or no
  impact registers. Loaded tree tiles are never evicted (~5 MB a forest tile) — the ceiling on a
  very long drive.

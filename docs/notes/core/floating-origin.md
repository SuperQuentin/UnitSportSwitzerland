# Floating origin (#185)

- **World space follows the camera** (`Core/OriginShifter`, `Core/WorldOrigin`): past 3 km
  horizontally from the origin, the origin moves to the camera snapped to whole km, and everything
  follows in one synchronous call at the start of a frame (`ProcessPriority = int.MinValue`).
  Altitude never shifts. Plan and later phases: `docs/plans/floating-origin.md`,
  `docs/plans/spherical-world.md` (#187).
- **Online too, and every peer has its own origin**: positions cross the network in LV95, never
  world space (`net/positions-on-the-wire`). The server keeps the manifest's origin and never
  shifts (its players can be anywhere); it measures with what players publish (`FootPlayer.Global`),
  and runs each race in a frame of its own at the host (`RaceManager`). A client's origin is its
  own from boot: joining neither checks nor adopts the server's.
- **A world-space `Vector3` means a place only in the frame it was computed in.** Anything that
  keeps a position across frames either keeps a `GlobalPos` (LV95 doubles), keeps it relative to its
  tile (trunks, bird perches, gathering trees are stored as the `.trees` file has them), or
  implements `IOriginShiftAware` and moves it in `OnOriginShifted`. Directions and velocities go
  through `shift.Direction`, points through `shift.Point`, placements through `shift.Apply`: the
  shift is a `Transform3D` so the round world (#187) can add a rotation without touching handlers.
- **Which nodes move**: children of an `IOriginContainer` (managers that never set their own
  transform: `ChunkManager`, `HorizonLayer`, `Traffic`, `VehicleManager`...) or of the
  `origin_container` group (the two `Players` nodes), children of plain `Node`s, and every
  `TopLevel` node. A container stays at the identity. A new manager that parents world objects
  under a Node3D must be a container; `--originstress` warns when it moves a node at exactly the
  identity that has spatial children. UI and `OwnWorld3D` viewports are skipped, but handlers
  under them are still called (the GPX zoom inset is a `CanvasLayer`).
- **Worker threads take `origin.Frame` once** (an immutable `OriginFrame`) and work in it; the
  main thread maps the result with `origin.Since(frame)` (`Traffic`'s lane graphs, `RaceRoute`).
  Reading the live origin twice from a worker can straddle a shift.
- **Shared point lists know their frame**: `RaceRoute`, `RaceLine` and `RaceCourse` record the frame
  their points are in and `Follow(origin.Frame)` moves them once, however many holders call it (a
  runner, its pilot, an NPC's driver share one route). `AutoPilot` also follows its route at every
  step, so a pilot whose owner does not (a probe) still drives in the right frame. A line surveyed off-thread (`RaceLine.Widen`)
  comes back in the frame it started in and is followed before it is swapped in.
- **Keys must be global**: lane graph junction keys and gathering spot names are LV95 cells. A key
  built from world coordinates names another place after a shift (and on another client).
- **Jolt does not teleport a kinematic body whose transform is set**: it sweeps it there with
  `MoveKinematic` during the next step, so for that step its collision is still at the old place
  and it moves at the whole shift per step. A parked vehicle (a `CharacterBody3D`) 31 m away stood,
  in the physics world, right under the shifted player and swept off at 1,860 m/s carrying them.
  The shifter makes every kinematic body static, sets its transform and makes it kinematic again
  (`OriginShifter.Teleport`). Removing it from the space and re-adding works too.
- **Shader patterns read `pattern_xz(world_pos)`** (`shaders/world_origin.gdshaderinc`): world XZ
  plus the `world_origin_offset` global, the origin modulo 9.6 km (a multiple of every pattern
  period). Raw `world_pos.xz` in a pattern jumps at every shift; normals from `dFdx(world_pos)` and
  portal clipping are fine as they are (they want small local values). Uniforms holding world
  positions that are not pushed every frame must be re-pushed on a shift (`cover_origin`, the
  building shader's occupancy and open-door boxes).
- **Cost**: ~5 ms per shift with ~1,500 nodes moved (headless), once every few km.
- **Checks**: `--origincheck` (headless: frames, lane keys, the wire between two origins, a race
  road followed across a shift, a remote interpolated 3,600 km out, the node walk, the kinematic
  case; RESULT line). Multiplayer: the `tools/*check.sh` loopback scripts with `--originstress` on
  the clients, and `--netsmooth` (records LV95). `--originstress <m>` shifts past `m` metres, to the metre: run any probe with it,
  e.g. `--ride car:1,25 --originstress 20`, `--flycheck plane --originstress 20`, and compare with a
  plain run. `--originshift <m>` changes the threshold and keeps the km snap. A probe that keeps a
  world `Vector3` start point reports nonsense distances under stress: give it a `GlobalPos`.
- **`--shot` / `--shot-queue` coordinates are world space as the game started** (the manifest's
  origin, or `--origin E,N`, which pins only that start), as before #185: `ShotRunner` maps each from
  that first frame when it aims, so a queue keeps meaning the same places however far the origin has
  moved since. `--originshift 1000000` keeps the origin still (a precision screenshot "before").
- **Seen in a window** (Riddes, 86 km from the manifest origin, `--time 12`): with the origin there,
  vine rows and flat-shaded facets are clean; with it 86 km away (as before) the vine rows break
  into horizontal streaks, the ground shading becomes per-pixel noise and house walls sparkle.

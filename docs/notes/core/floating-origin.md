# Floating origin (#185)

- **World space follows the camera** (`Core/OriginShifter`, `Core/WorldOrigin`): past 3 km
  horizontally from the origin, the origin moves to the camera snapped to whole km, and everything
  follows in one synchronous call at the start of a frame (`ProcessPriority = int.MinValue`).
  Altitude never shifts. Plan and later phases: `docs/plans/floating-origin.md`,
  `docs/plans/spherical-world.md` (#187).
- **Online too, and every peer has its own origin**: positions cross the network as ECEF, directions as local ENU, never
  world space (`net/positions-on-the-wire`). The server keeps the manifest's origin and never
  shifts (its players can be anywhere); far clusters of bodies get a frame and a physics space of
  their own (`net/server-regions`, #1456); it measures with what players publish (`FootPlayer.Global`),
  and runs each race in a frame of its own at the host (`RaceManager`). A client's origin is its
  own from boot: joining neither checks nor adopts the server's.
- **A world-space `Vector3` means a place only in the frame it was computed in.** Anything that
  keeps a position across frames either keeps a `GlobalPos` (ECEF doubles, `core/globalpos-earth`), keeps it relative to its
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
  A mesh rebuilt from world points under a container (the traffic's debug lines, `ImmediateMesh`) is
  moved by a shift like any child: put its node back at the identity each time it is redrawn in the
  new frame, or every line lies a shift away (the trails "broke after moving on", `--origincheck`).
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
- **The Earth's curvature is drawn round the camera, not the origin** (#187, `world/earth-curvature`):
  a shift changes nothing in it.
- **Shader patterns read `pattern_xz(world_pos)`** (`shaders/world_origin.gdshaderinc`): world XZ
  plus the `world_origin_offset` global, the origin modulo 9.6 km (a multiple of every pattern
  period). Raw `world_pos.xz` in a pattern jumps at every shift; normals from `dFdx(world_pos)` and
  portal clipping are fine as they are (they want small local values). Uniforms holding world
  positions that are not pushed every frame must be re-pushed on a shift (`cover_origin`, the
  building shader's occupancy and open-door boxes).
- **Cost**: ~5 ms per shift with ~1,500 nodes moved (headless), once every few km; 22-47 ms in a
  window at Riddes (~530 nodes, 120 kinematic), so the frame that shifts takes 50-90 ms.
- **The renderer across a shift** (phase 4, measured in a window at Riddes, RTX 4070, 1600x900,
  a `shift 1000,0` shot-queue line with the camera still):
  - **SDFGI** (Realistic+ with `--sdfgi`, Forward+): its cascades sit on the world grid round the
    camera, so a shift re-voxelises all of them. One frame takes 25-37 ms of GPU instead of 8-12,
    the next is back to normal. No dark flash: the frame after a shift differs from the one before
    as little as without SDFGI. The light then drifts for ~1 s to a slightly different result
    (house walls, ~1/255 on average): the coarse cascades' cells (up to 12.8 m) do not divide
    1 km, so they land differently on the geometry. Nothing to fix; a weaker GPU pays more for
    that one frame (SDFGI is 15-20 ms per frame on an M1 Pro).
  - **Shadows** move by less than a texel for one frame (the directional shadow map snaps to world
    space). No reflection probes, decals or lightmaps exist.
  - **Particles** are world space (Godot's default): those alive at a shift stay at the old place,
    1-3 km away. All are bursts of 2 s or less except an explosion's smoke (5 s) and the BR crate
    smoke (9 s, continuous), which thin out for a few seconds. Local coordinates would fix it for
    an emitter that stays put, but rotate its gravity and drift with the node, and a falling
    airdrop's trail needs world space: left as is.
  - **Falling snow** wraps round the camera in a 60 m box: in pattern space (`world_origin_offset`,
    9.6 km = 160 boxes), or every flake lands somewhere else at a shift.
- **Shifts that turn** (#1430 H3, `docs/plans/ellipsoid-handover.md`): an anchor frame
  (`OriginFrame.Axes`, `FrameAxes`: an origin and axes on the Earth) is reached by a shift with a
  rotation. `OriginShift.Turns` and `Yaw` (radians about up) say so. What a handler must do then:
  - the shifter turns `RigidBody3D` linear and angular velocities, and `CharacterBody3D.Velocity`
    of a body that is not `IOriginShiftAware` (one that is turns its own);
  - a heading kept as an angle adds `shift.Yaw` (`FootPlayer._viewYaw`, `SpectatorCamera._yaw`,
    `FlightMotion.Yaw`); a flight's velocity and attitude turn with `Direction` / the basis
    (`FootPlayer`, `VehicleBody` handlers);
  - a ride with world-space state of its own overrides `Rideable.OnOriginShifted` (the boat turns
    its velocity, spin and attitude; its position is read back from the body);
  - a shader sampling by world XZ gets the axes too (`HorizonLayer`'s `cover_axes`);
  - a check steering by compass heading turns it by the grid's yaw in the world (`BoatCheck.GridYaw`).
  - **In Space the shifts turn above 250 km** (#1480, `SpaceMode.Rotates`, `Curvature.TurnRegime`,
    `OriginShifter.Turned`), where no flat tile is drawn any more: every 20 km the anchor is
    `FrameAxes.Tangent` under the camera, built in ECEF (the previous axes turned by the least rotation, no
    LV95). From 60 to 250 km the Space frame stays flat: the flat tiles are still drawn there, and the
    curvature shader wraps them as one plane. The flat world's ground keeps its flat frame until G2 D.
  - **The earth world's ground turns** (#1512, G2 C3, `--frame earth`, `OriginShifter.GroundMode`): past 3 km
    (horizontally) from the anchor, the world moves to `FrameAxes.Tangent` under the camera at altitude 0 (the
    camera's LHN95 height as the drop, as the Space anchor's), its axes the old ones turned by the least rotation:
    0.027° each 3 km, no yaw. Up and gravity (−Y) are then the ellipsoid's under the camera, never more than
    0.027° off. No snap: everything placed from doubles (leaves, patches, the globe) is placed again from the
    frame; what is moved in floats drifts by the float basis (0.1 mm 30 m out, ~2 cm on a body left 57 km away
    over 19 shifts: such a body is dormant, kept by its Earth place). One frame at every altitude: above 60 km it
    re-anchors past `SpaceMode`'s 20 km, with no handover (the flat world's band and its two rigid moves exist
    because its tiles are flat). A teleport (`ShiftTo(e, n)`) lands on the tangent anchor under the point.
    The shift log line says how a shift turned (`turned: tilt 0.0269°, yaw 0.0000°`). `OriginShift.Turns` is exact
    (a 20 m stress shift turns 3·10⁻⁶ rad, under `IsEqualApprox`); `Apply(Aabb)` bounds the moved corners.
    `--originstress <m> --originyaw <deg>` there turns the tangent anchor by ±deg, one way then back.
    `--orbitcheck` flies a polar orbit through ~2,080 turned shifts; `--spacebandcheck` the band.
  `--originstress <m> --originyaw <deg>` sends every shift to an anchor frame at altitude 0 turned
  ±deg about up, so every handler meets a rotation: the car ride, the A320 and the hairpin drive
  pass at 2° (check map). Known gap: the speedboat's beach landing (`--boatcheck`) stops 3 m
  further out and keeps creeping at 1.2 m/s in a turned frame, everything else on the boat passes.
- **`--world flat` has no shifter**: its probes' `--originstress` changes nothing (no `[origin]` line). Stress a
  handler on real terrain (`--chunks`) or under `--frame earth --leaves`.
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

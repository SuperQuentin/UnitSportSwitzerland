# Snowboard (#922)

- **One snow step**: `SnowRide` (`src/Player/SnowRide.cs`) is the base of `Skis` and `Snowboard`:
  `Glide` runs `SkiDynamics` (`player/ski-physics`). Every on-snow check (lifts, slope races,
  slope HUD and map, snow wear, autopilot presses, XR lean, sounds and spray, interest size, trailer
  steering, surface sampling) asks `SnowRide.Is(kind)` or `is SnowRide`, never `Skis` alone. A new
  snow ride only needs its `RideKind` in `SnowRide.Is`.
  - Missed once: `FootPlayer` samples the ground surface only for some rides; without `SnowRide`
    there the board rode on "asphalt" (off-snow friction) and never got going.
- **Tuning** (`SkiDynamics.Tuning.Board` / `BoardGame`): carve window 0.10, more bite at full
  edge, drag area 0.70 standing sideways, a push of 0.8 m/s capped at 2.5 (off snow 0.5, cap 1.0),
  `PloughDecel` 1.5 (the back edge dragged when slow). Toe edge grip x1.1, heel x0.9
  (`BoardEdgeGrip`, `ToeSide(bank, facingRight)`; `Bank` < 0 is a lean to the right).
- **Brake**: above 2 m/s the board swings to a quarter turn (it stops pivoting at 1.45 rad of slip)
  and skids on the trailing edge: without steer the heel side (chest to the travel), with steer to
  the side steered.
- **Hop turn (#954)**: brake in the air swings the board across as on the snow (to the side steered,
  else heel side) at `SkiDynamics.HopTurnRate`, to `StopAngle`; landed, it skids on its edge (5.7 m/s
  to a stop in 1.35 s, `--ride board --hopturn 2.5`).
- **Switch**: `SkiDynamics.Switch`: tail first (|slip| > pi/2 + 0.2) above 1.5 m/s turns the
  heading half a turn and the slip with it; the velocity is unchanged and the rider keeps facing
  the same way (`FacingRight = Goofy == Switch`).
- **Stance**: `GameSettings.Goofy` (Settings > Gameplay, `--stance goofy`). Stance and switch ride
  in the replicated `FootPlayer.TuningBits` (`Snowboard.Bits`: bit 0 goofy, bit 1 switch); the
  owner writes them after each step, every peer rebuilds the visual when they change.
- **Visual**: `SnowboardMeshBuilder.Snowboarder`: the board (twin tip, highbacks on the heel side)
  and `HumanPose.Boarding` (feet at +-0.27 m on the bindings, arms out) turned a quarter turn, the
  chest to the toe edge. In the model viewer under Rides. First-run intro card
  `VehicleIntroKind.Snowboard`; race mount word `snowboard`.
- **Feel (#933)**: `MaxYawRate` 1.7 (Game 1.95), `BankResponse` 4.5 (Game 5.5); Shift
  straight-lines (steer x0.3). The rider is a live `SnowRig` (`avatar/snow-rig`): toe and heel
  lean, the back foot pushing, arms balancing, angulation.
- **Checks**: `SnowboardDynamicsTests`; `--ride board,25,out.png --at 2580350,1100050` (with
  `--chunks` from a worktree): top 9.1 m/s against the skis' 10.7 on the same line.

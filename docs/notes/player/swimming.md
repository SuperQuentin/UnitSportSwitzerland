# Swimming, diving and the air reserve (#301)

- **An on-foot state** (`Player/FootPlayer.Swim.cs`), one dispatch line in `_PhysicsProcess` after
  the mantle: `if (SwimPhysics(dt, onFloor)) return;`. It reads the water only through
  `World.WaterField` (`docs/notes/world/water-field.md`).
  - **In**: feet more than `SwimEnter` (1.35 m) under the surface (the chest is under), or falling
    (`Velocity.Y < -1`) into water deeper than that. **Out**: standing on the bed with less than
    `SwimLeave` (1.15 m) over the feet (wading out), off the water layer's edge, more than 0.6 m
    clear of the surface, a mantle, or any ride (`ApplyRide` calls `LeaveWater`).
  - **Floating**: feet `SwimFloat` (1.42 m) under the moving surface (eye 0.26 m out), the
    surface particle's velocity (`WaterField.Velocity`) added, weighted to nothing 1.5 m down. A
    gamey swell (1.8 m) carries the swimmer within 0.24 m of that depth (`--swimcheck`).
  - **Controls**: stroke 1.5 / sprint 2.4 m/s (Game; Sim 1.15 / 1.9). Crouch dives (1.5 m/s, 2.1
    sprinting), Jump rises (1.7 m/s) under water and is a lunge at the surface (2.6 m/s up, for a
    ledge just out of reach). Under water, or looking down steeply (pitch < -0.6) while stroking
    forward at the surface, the stroke goes along the look (yaw and pitch). Idle, buoyancy lifts
    0.45 m/s, faster just under a crest so a rising swell does not leave the swimmer behind.
  - **Velocity model**: the velocity relative to (stroke + water) decays by
    `exp(-(2.5 + 0.25 |v|) dt)`; a body bobbing above its floating depth and not plunging falls
    under gravity. **The water brakes a plunge from the moment the feet are in**: an early
    version only braked past 1.07 m and a 30 m drop into 1.6 m of water hit the bed at 21 m/s.
  - **Climbing out**: pushing into a wall at the surface runs `TryBeginMantle` (no Jump needed, as
    in the air). Feet are 1.42 m down, so a ledge up to ~0.7 m above the water climbs as it is,
    ~1 m with a lunge.
- **Water landings**: no special case, the drag does it. Hitting the bed faster than 11 m/s hurts
  like a landing (`(v - 11) * 9`). Measured: a 30 m drop into 25 m of water costs nothing, into
  1.56 m of water 39 (on land 124). Wingsuit and canopies: `FlyerIntoWater` (first line of
  `FlyPhysics`) puts the pilot in the water swimming, "SPLASHDOWN", no crash. Bikes and skis
  deeper than the chest: off and swimming (`RideIntoWater`, in `WaterPhysics`). A crash ragdoll
  in deep water floats as a ragdoll (#380, below), then comes round swimming. A sunk car (`vehicles-sink`): the driver comes out at the surface swimming
  (`StartSwimmingAtSurface`), passengers through `ThrownOut` → `SurfaceIfInWater`.
- **A crash ragdoll in the water (#380)**: `Ragdoll.Step` takes a `WaterProbe` (the surface and
  `WaterField.Velocity` over each point, asked once a frame, not per substep). Each point is buoyed
  by the share of it under the surface (over 0.24 m) times its buoyancy over weight (chest 2.2,
  waist 1.4, hips 1.1, shoulders 1.15, head 1, limbs 0.97: 1.08 all told) and dragged toward the
  water's motion (`1.0 + 0.6 |v|` per second, the flow fading 1.5 m down). Measured (`--swimcheck`,
  4 m up into 25 m of water on a chop): the hips plunge 1.8 m, are back up by the surface ~4 s after going in, float limp
  face down 0.1-0.3 m under the moving surface and drift ~0.6 m. The first buoyancy (0.99 all told)
  sank slowly and stayed 2 m down. **Coming round** (owner, `RagdollIntoWater` in `FootPlayer.Swim.cs`):
  2.5 s floating by the surface (hips < 0.6 m under; a plunge past 1 m restarts the count) and moving
  with the water (< 0.9 m/s off it), or 6 s floating, or 10 s in deep water, whatever it does; then
  `StartSwimming` at the surface. A ragdoll in deep water never ends by resting (`_ragdollInWater`).
  A splash where the hips go in, on every peer (`RagdollSplash`). **Remote peers** run their own copy
  in their own waves (`TickRagdoll` passes the probe on every peer) and are steered onto the owner's
  hips as on land; the owner's swim pose ends it. `tools/swimnetcheck.sh`: B's copy floats 0.30 m
  under B's surface where A's floats 0.23 m under A's. Nothing is held in a limp hand
  (`HeldItemVisual`: a held item hung in the air where the hand was, in any crash).
- **Wading (#380)** (`FootPlayer.Wade.cs`, numbers in `Wading.cs`, unit-tested in `SwimTests`): the
  feet's depth under the moving surface (`WadeDepth`, owner, set in `SwimPhysics` before the swim
  test) slows the walk from 0.2 m to waist deep (1.05 m), a run more than a walk (drag grows with
  speed): `--swimcheck` measures 1.00 / 0.85 / 0.43 of the walking pace at 0.13 / 0.49 / 0.99 m
  (1.9 / 1.6 / 0.8 m/s, Game) and 1.00 / 0.78 / 0.23 of the run (1.3 m/s at the waist). No slide
  deeper than the knees. Spray round the shins and foam left on the waves (`WakeFoam`, every peer
  from its own waves and the copy's stride speed `Anim.X`), and a slosh a stride
  (`SfxSynth.WadeBank`: the owner's footstep is replaced in `PlayerFeel`, remotes heard spatially at
  the stride cadence).
- **Air** (`Player/AirReserve.cs`, plain C#, `SwimTests`): 45 s with the eye under, 1.7x on a
  sprint stroke, refills 9 s/s with the head out, empty = 15 health at once then each second
  (`DamageCause.Drown`, appended). Knocked out in the water the body floats face down, then wakes
  at the last safe spot; **a safe spot is never recorded while swimming** (`TickHealth`). A gasp
  coming up below half. HUD: a blue bar over the health bar (`PlayerFeel`), only while not full,
  blinking red under a quarter. Any ride refills it.
- **Pose, replicated with what already existed** (`what-others-see-what-owner`): `PoseKind =
  PoseSwim` (4), `Anim = (stroke speed, stroke phase, SwimStyle, 0)`, `BodyPose` lays the upright
  figure down about the chest (1.25 m): tread -0.12 rad, crawl -1.45, under water along the stroke
  (straight down = head down), a plunge with no stroke stays feet first, out cold face down.
  `IsSwimming` on a remote copy reads `PoseKind`. The figure (`Avatar/HumanMeshBuilder.Swim.cs`,
  `SwimStyle` append-only): tread (sculling, egg-beater), crawl (alternate arms, six-beat flutter,
  body roll), under (breaststroke pull, frog kick, glide); under water and not stroking it treads.
  Drawn by `ApplySwimFigure` (from `ApplyFootPose`), rebuilt in place on a new `FootPoseKey`
  (`perf-pose-mesh-cache`); a remote integrates the phase between updates (`AdvanceSwim`).
- **Fx**: a spray burst where a body goes in (every peer, `SplashAt`; a remote's the first frame it
  is drawn swimming), `SfxSynth.SplashBank`/`StrokeBank`/`GaspBank` (owner through `PlayerFeel`,
  remotes spatial), the swim hint (dive / up) for the first 5 s. The underwater look and muffle
  are #299's (`WaterSurface`, by the camera).
- **Items holstered**: `ItemController.UsablePlayer` is null and `HeldItemVisual` hides the hand
  while `IsSwimming`; the stack stays selected.
- **For boats (#302)**: `IsSwimming`, `StartSwimming(at, velocity)` (gets off any ride, false when
  no water or a seated passenger), `StartSwimmingAtSurface(at)`, `SwimDepth`, `HeadUnderwater`,
  `Air`. Boarding from the water: whatever boards calls `ApplyRide`, which ends the swim.
- **Shared-file hooks** (for merges): `FootPlayer.cs` `_PhysicsProcess` (one line), `PublishFootPose`
  (one line), `ApplyFootPose` (one line), `FlyPhysics` (one line), `ApplyRide` (`LeaveWater()`),
  `_Process` (`TickWade`, first line, #380), the walk's pace (`* WadePace(running)`, #380),
  `TickHealth` (`!_swimming`), `DamageCause.Drown`; `FootPlayer.Water.cs`, `.Passenger.cs`, `.Crash.cs`
  one call each.
- **Limits**: legacy tiles (today's real map) have 0.12 m of water, so nobody swims there until
  #298's beds (and nobody wades: 0.12 m is under the 0.2 m the walk feels); the server does not know who swims (owner
  authority, like the walk); NPC drivers thrown into water float, they do not swim.
- **Checks**: `--swimcheck --chunks fixture:lake` (quick, headless, ~2.5 min: walk in, sprint and
  easy stroke speeds, a gamey swell, look-down stroke, dive to the bed 7.4 m down, rise, float up,
  air refill, a pontoon climbed out onto, wading out, drowning and waking on the beach, a sunk
  car's driver, high dives deep and shallow, a wingsuit onto the lake, wading paces at three
  depths, a crash ragdoll into the lake on a chop). `--swimonly wade,ragdoll` runs only those steps
  (names in `SwimCheck`'s summary). `--swimcheck shots` windowed: PNGs of each step in
  `test_output/swim/` (`wade_knee_side`, `wade_waist_side`, `ragdoll_float_close`).
  `tools/swimnetcheck.sh` (net: a loopback server, A swims, B 7 m away must see A's copy in the swim
  pose, the right style and A's depth to 0.35 m at the surface, 1 m under water; then A is thrown
  limp into the lake (`DebugThrow`) and B sees the copy go limp, float as deep as A's to 0.45 m and
  swim; `SHOTS=1` B windowed: `test_output/swimnet_B_*.png`).

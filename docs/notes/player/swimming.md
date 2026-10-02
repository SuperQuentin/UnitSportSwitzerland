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
  that goes into deep water stops and floats (`RagdollIntoWater`, in `TickRagdoll`); it is not
  simulated floating. A sunk car (`vehicles-sink`): the driver comes out at the surface swimming
  (`StartSwimmingAtSurface`), passengers through `ThrownOut` → `SurfaceIfInWater`.
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
- **Hulls overhead** (#378, `OutFromUnderHull` after the swim's slide, `ClearOfHull` on the
  stroke, in `FootPlayer.Boat.cs`): against a vehicle's hull that overhangs the swimmer, facing
  down (come up under a ship's bottom, a boat's bow dropping on the swell), it strokes out across
  the hull toward its nearer side at 1.5 m/s at least; under a flare (the steamer's topsides) the
  stroke loses what drives it in under it. Stroking into the steamer's side, the flare slid the
  swimmer down and held its head under for 3 s; the buoyancy held one that came up under the
  hull against its bottom for good.
- **For boats (#302)**: `IsSwimming`, `StartSwimming(at, velocity)` (gets off any ride, false when
  no water or a seated passenger), `StartSwimmingAtSurface(at)`, `SwimDepth`, `HeadUnderwater`,
  `Air`. Boarding from the water: whatever boards calls `ApplyRide`, which ends the swim.
- **Shared-file hooks** (for merges): `FootPlayer.cs` `_PhysicsProcess` (one line), `PublishFootPose`
  (one line), `ApplyFootPose` (one line), `FlyPhysics` (one line), `ApplyRide` (`LeaveWater()`),
  `TickHealth` (`!_swimming`), `DamageCause.Drown`; `FootPlayer.Water.cs`, `.Passenger.cs`, `.Crash.cs`
  one call each.
- **Limits**: legacy tiles (today's real map) have 0.12 m of water, so nobody swims there until
  #298's beds; no wading slowdown in the shallows; the server does not know who swims (owner
  authority, like the walk); NPC drivers thrown into water float, they do not swim.
- **Checks**: `--swimcheck --chunks fixture:lake` (quick, headless, ~2.5 min: walk in, sprint and
  easy stroke speeds, a gamey swell, look-down stroke, dive to the bed 7.4 m down, rise, float up,
  air refill, a pontoon climbed out onto, wading out, drowning and waking on the beach, a sunk
  car's driver, high dives deep and shallow, a wingsuit onto the lake). `--swimcheck shots`
  windowed: PNGs of each step in `test_output/swim/`. `tools/swimnetcheck.sh` (net: a loopback
  server, A swims, B 7 m away must see A's copy in the swim pose, the right style and A's depth to
  0.35 m at the surface, 1 m under water; `SHOTS=1` B windowed: `test_output/swimnet_B_*.png`).

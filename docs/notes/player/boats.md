# Boats: the boat model, the jetski and the speedboat (#302)

- **A boat is a `Rideable` of its own** (`Player/Boat.cs`), not a `Flyer`: like one it owns a full
  3D motion (`BoatState`: centre of mass, velocity, angular velocity, attitude, engine) posed on
  the upright, yaw-only body that carries it (`Boat.Pose`, about the centre of mass); unlike one it
  has seats, a hull that collides as drawn (`FitHull`, from `HullLift` ~ the waterline up) and is
  left in the world floating. `RideKind` **121 Jetski, 122 Speedboat** (`BoatCatalog`, append-only;
  the steamer #303 is 123). The body's origin is the **keel under the centre of mass**, so the
  capsule meets the lake bed where the hull does.
- **The model** (`Player/BoatModel.cs`, plain C# over Godot maths, `BoatTests`): `BoatDynamics.Step`
  (2 substeps) against an `IBoatWater` (`BoatWater`: `WaterField` at one wave time per step, the
  flow (orbital velocity + Stokes drift) once per step, the bed from the height grid, `Wind` zero
  until #304). Per **hull column** (`HullColumn`: foot, width, length, height; a coarse voxel set):
  buoyancy of the water it displaces (works heeled and upside down), a slam/damping force from the
  water rising up it (capped at 10 m/s: a hull put down by hand "slammed" at 60 m/s and exploded),
  the side's bite (lateral drag), the bed as a stiff damped contact with friction. Globally: drag
  along the hull, **calibrated to a published top speed** (`Drag2` solves full thrust = drag + air +
  the plane's lift leaning back), a **hump** in it (`x² e^(1−x²)` of speed over `HumpSpeed`),
  **planing lift** (`LiftShare` of the weight by `PlaneSpeed`) **spread over the wet columns** (at the
  centre of mass alone the plane had no pitch stiffness and the bow dug in), normal to the bottom
  (banked, it pulls the boat round), plus a **wave-face kick** (`FaceKick`: the water's slope under the
  hull fore and aft against the running trim, ripples under 1.5 % ignored, applied at the centre of mass:
  spread over the columns it all went into the bow and flipped the jetski), **trim** as the hull's own wave tilting the water it floats on
  (`HumpTrim`, `PlaneTrim`, rad: a torque fought the buoyancy and gave 1-2°), thrust from a prop at
  `ThrustAt` (none out of the water: the engine races) or a jet whose nozzle turns the thrust (no
  thrust, no steering), a **rudder and skeg** as fins lifting with the flow past them (way + prop
  wash) at the helm plus the stern's sideslip (with no skeg the jetski wandered 20° in 1.5 s; with
  too much it would not turn), leaning into a turn (a damped spring on the roll toward
  `BankPerG`·v·ω/g), the wind on `WindArea`.
- **Rider thrown off** (`ThrowsRider`, the jetski): a landing into the water faster than
  `ThrowLanding`, a crooked one (`ThrowTilt`) faster than a third of it, or rolled past `FlipAngle`
  for 0.3 s. `FootPlayer.ThrownFromBoat`: the machine is parked riderless (engine cut, lanyard),
  everyone aboard out (`PassengerService.Wrecked`), the rider **swimming** beside it
  (`StartSwimmingAtSurface`, #301). Getting out over the side swims too (`ExitVehicle`).
  **Kept harsh on purpose (decided 2026-10-02):** flat out into a gamey swell the jetski throws
  its rider within ~20 s. It is funny, so it stays for now; revisit it (raise `ThrowLanding` /
  `ThrowTilt`, or scale with sea state) if players find it frustrating.
  **Boarding from the water**: E swimming beside the hull claims it like any vehicle (`--boatcheck`).
- **At the helm** (`FootPlayer.Boat.cs`, one dispatch line in `_PhysicsProcess`): the model steps
  from where the body is, the body follows its path through `MoveAndSlide`, what the world took off
  is an impact (> 4 m/s dents it, > 13 m/s wrecks it); the capsule on the ground drags the hull
  (`BoatDynamics.Beached`, μ 0.5: the capsule held the keel up and a jetski slid 40 m up a beach).
  `FloorSnapLength` 0 (snapped, a hull in a metre of water sat on the bed). `_motion` is a
  projection (speed, heading, yaw rate, half the roll) so the chase camera, HUD and feel layer work
  unchanged; first person rides the hull's pitch and half its roll. HUD: kn, km/h, rpm, ASTERN,
  AIR / AGROUND / PLANING. Sound: `IEngined` (`EngineSynth`: the jetski's triple, the runabout's V8).
- **Left in the world** (`VehicleBody.Boat.cs`): the same model with no helm; it floats, drifts
  (Stokes drift + orbital flow: ~0.1 m/s gamey), runs aground, and **sleeps** when its speed and spin
  are small and the swell where it floats is under 6 cm (calm). Its attitude rides in `Tilt`, its
  height over the waves in `Heave`; parked boats' attitude travels as `VehicleState.Angles` (Euler).
- **Moored** (#378, `BoatDynamics.Moor`, after the driverless step): pulled softly back to its spot
  and heading against the drift, as lines to a berth would: a critically damped spring (0.5 rad/s, a
  ~13 s swing back), at most 0.6 m/s² (shoved far off, it comes back slowly, not flung), the heading
  likewise (0.5 rad/s, 0.3 rad/s²); level only, so it still heaves, pitches and rolls (and its
  collision with it). An acceleration, not a force: the same for a jetski and the 518 t steamer. A
  boat left in the world moors itself where it comes to rest (under 0.6 m/s; one left running moors
  where it stops); `VehicleBody.Moor(GlobalPos, yaw)` / `Unmoor()` for a berth or #379's AI steamer
  at a landing (the spot is origin-free). Taking the wheel ends it (the parked body goes; parked
  again, it moors where it is left). Measured: `BoatTests` with 0.16 m/s of drift, 60 s gamey: jetski
  0.89 m off at worst, speedboat 0.66 m, steamer 0.28 m (free: 8 m); `--boatcheck` a minute moored
  gamey within 1 m (speedboat 0.36 m, heading 1°).
- **Its collision is where it is drawn** (#378, `VehicleBody.PoseHull`, from `DrawBoat`): the hull's
  shape (the parked box, the steamer's shaped hull) takes the drawn frame's pose every frame, heave,
  pitch and roll, on every peer: the authority from its model, a copy on its own waves at the sent
  height and attitude. A headless peer (the server, checks) poses an empty `Visual` frame the same
  way, for every boat now (it was the walkable steamer only). Only the shape moves inside the body,
  never the body (nothing for Jolt to sweep), and only when its middle or far corner moved a
  centimetre (a boat asleep in a calm costs nothing). A level box let the bow rise through a
  swimmer's head and left a player standing on air beside a rolled hull. Swimmers under a hull or a
  flare are stroked out from under it (`swimming`, "Hulls overhead").
- **Nothing it rests on carries it** (#738): a parked boat's `PlatformFloorLayers` is 0. Players
  share layer 1 with the ground, and a swimmer under the hull teleported away (respawn, a probe's
  `StartSwimmingAtSurface`) gave Godot a one-step platform velocity of ~1700 m/s: the steamer
  jumped 28 m with the swimmer, and `--steamercheck`'s ladder step found no ladder.
- **What others see** (`what-others-see-what-owner`): the owner sends `Anim = (rpm, thrust share,
  Heave, wet + 2·airborne)`. **`Heave` = the body's height over the mean surface under the hull's
  centreline** (`Boat.TrySurface`, three points). A remote copy (`FootPlayer._Process`,
  `Boat.RemoteY`) and a parked copy (`VehicleBody.DrawBoat`) draw it at that height over **their own
  copy of the waves at their own time**, so however far behind the position stream is, the boat sits
  on the waves that peer draws; attitude comes as is (`BodyPose`, `Tilt` eased). Measured on
  loopback (`tools/boatnetcheck.sh`, gamey): the copy's pitch against B's surface slope correlates
  0.98 / 0.90 / 0.99 (idle / running / parked) where A's own boat does 1.00 / 0.93 / 0.99 on A's;
  keel depth under the surface 0.276 m on B, 0.277 m on A.
- **The steamer (#303)** is built on this model: `BoatDrive.Paddle` (two wheels on one shaft that
  reverses through stop at the engine's pace), `LinearDrag` per kg (a 500 t hull at the small boats'
  0.05 could not make 3 m/s), `RudderAt` (a rudder away from the thrust), see `steamer`. A boat's body
  is not lifted when its capsule grows (`ApplyRide`: a steamer taken from its deck rose 2.8 m and fell
  back in), and a shove faster than the model is never adopted as its velocity (only ever slower).
  Cost: one `WaterField.TryLevelAt` per column per substep (the steamer: 56 columns).
- **Measured** (`--boatcheck`, calm unless said): jetski draft 0.23 m, on the plane in 1.4 s, top
  82 km/h (spec 81), bow up 7° over the hump, running trim 2-3°, circle 59 m at 64 km/h banked 11°;
  speedboat draft 0.28 m, plane 3.2 s, top 70.5 km/h (38 kn), hump 6.6°, trim 3°, circle 55 m at
  52 km/h. Gamey flat out **into the swell** (west; running with it, east, a hull hardly leaves the water):
  the jetski leaves crests for up to ~1 s, keel up to 1.5 m clear, and usually throws its rider within
  20 s (a crooked or hard landing); the speedboat ~0.7 s, never thrown. Parked gamey: heaves 1.3-1.7 m, keel within 5 cm of
  its draft under the surface.
- **Checks**: `tools/test.sh unit` (`BoatTests`: floats level, rights itself, hump and plane and top
  speed, turns without capsizing, a jet does not steer off throttle, gamey, parked drift, beaching);
  `--boatcheck jetski|speedboat[,shots] --chunks fixture:lake` (quick; `shots` windowed: pictures
  in `test_output/boats/`, `hull_touch`/`hull_side` with the collision box drawn in magenta);
  `tools/boatnetcheck.sh` (net; `SHOTS=1`: B's view in `test_output/boatnet_B_*.png`). Both swim into
  a parked boat's side in the gamey swell (`Player/HullTouch`, #378): the collision box within 3 cm of
  the drawn hull at every corner, tilted with it, the swimmer's contacts on it as drawn, not pushed
  under; `boatnetcheck` does it on B, against A's boat as B draws it.
- **Foam on the waves (#380)**: the wake, spray and jet (and the steamer's wake, bow wave and
  churn) are drawn by `shaders/wake_foam.gdshader` through `Avatar/WakeFoam` (one shared material per
  kind, every style): a patch sits on the surface over its own centre (the rest point found by three
  fixed-point steps of `water_wave_displace`, as `WaterField.TryLevelAt` does) laid on the tangent
  plane, 6 cm over it (the water mesh is the waves joined by straight lines every 2 m) and 4 cm toward
  the eye; a spray speck faces the camera and is gone under the surface. The still level and wave
  scale are per-instance uniforms written only on change (`WakeFoam.OnSurface`/`Water`). The flat
  wake used to cut through crests and hang over troughs. Not checked headless (no particles, no
  shader compile there): `--boatcheck speedboat,shots,wake --style ps1|cartoon` (windowed) puts the
  speedboat at half ahead across a gamey swell and takes `wake_swell_*` pictures into
  `test_output/boats/<style>/`, and logs the hull slaps.
- **Water slapping the hull (#380)** (`Avatar/HullSlap`, on `BoatRig` and `SteamerRig`, every peer from
  the hull it draws and its own waves): the water's height up the forward hull is watched frame to
  frame; climbing it faster than 0.1 m/s slaps (`SfxSynth.HullSlapBank`: the skin's knock, the smack,
  the wash), as loud as it climbed fast (full at 1.7 m/s), then 0.12-0.32 s before the next. Within
  120 m of the ears (`Audio/Ears`) only. Measured (speedboat): calm idle 0.2 faint laps a second, gamey idle 0.8
  (strength 0.03), gamey half ahead 1.2 at 0.2. The steamer's is pitched 0.55 and carries further.
- **The wheel turns (#380)**: the runabout's rim is its own node (`BoatMeshBuilder.RunaboutWheel`,
  `BoatRig.Steer`), turned `Wheel.Lock` (2.4 rad) at full helm, the driver's hands with it (one
  figure per 0.06 rad, kept); the steamer's wheelhouse wheel likewise (`SteamerMeshBuilder.HelmWheelMesh`,
  `SteamerRig.Steer`, 3.6 rad). The helm (`Boat.HelmNow`, -1..1 from `State.Helm`) travels in the
  pose's W as a whole number 0..100: `+ 4 × HelmSteps` for a boat (wet and airborne below it,
  decoded with `PosMod`), `+ 32 ×` for the steamer (above its whistle and gangway bits);
  `Boat.HelmOf` / `Steamer.WheelOf`. `--boatcheck` checks the owner's wheel turns (and pictures it
  straight and hard over with `shots,wake`); `tools/boatnetcheck.sh` checks B's copy turns as A's.
  The jetski has no wheel (bars on the rider's mesh, not turned).
- **At a jetty** (#377, `world/landings`): harbour jetties are solid decks; getting out beside one steps
  onto it instead of into the water (`FootPlayer.Pier.cs`, `--steamercheck pier|nyon`).
- **At the harbours** (#383, `world/landings`): jetskis and speedboats moored along the jetties, dormant slots since #554 (`DormantSlots.ForMarina`, drawn instanced until touched), put back a while after being taken.
- **Not done**: boats placed at the real harbours (the jetties are there, nothing parks at them); no hiss of a hull running through the water; boats in races have no water courses (the mount words
  `jetski`/`boat` parse); the jetski's rider is the motorbike rider (helmet).

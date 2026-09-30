# Player, mounts and flight (`src/Player/`)

On foot, mounts, bike, skis, flight, feel layer, tricks, Game/Sim profile. Input bindings live in `src/Core/CLAUDE.md`; left-in-world vehicles in `src/Vehicles/CLAUDE.md`.

## Architecture

- **Third / first person** (`FootPlayer`, **V / R3**, saved as `GameSettings.ThirdPerson`, default
  third; `--view first|third` for one run). On foot the mouse/stick turn a **view yaw**
  (`_viewYaw`), not the body: first person sets the body to it every render frame (the old
  behaviour exactly), third person lets the body turn to face its travel (`FaceTravel` — toward
  the input while there is some, else the velocity) and orbits a spring-arm camera
  (`UpdateThirdPersonCamera`) from above the right shoulder in **global** space — parented to the
  turning body it would swing round every direction change. Movement is relative to the view, so
  forward is into the screen in both. Both cameras update in `_Process`, not physics, or look lags
  the mouse by up to a physics tick. The local body is the same `HumanMeshBuilder` figure remote
  players see: solved gait grounded, `Running` pose airborne (>0.12 s), `Tucked` sliding, and the
  landing-dip spring spent as a squash. Mounted first person sits at the figure's own eye
  (`Rideable.FirstPersonEye` from `MountsForPose`), rolled with the lean.
  Screenshot the player's view with `--ride foot|bike|skis,seconds,out.png` (`foot` stands still).
- **Feel layer** (`Player/PlayerFeel`, child of the LOCAL `FootPlayer` only): sound, camera shake,
  speed lines, particles, pad rumble and a small HUD (km/h when mounted, "AIR x.x s" popup after
  >0.7 s airborne). It only **listens** — `FootPlayer` raises `Landed(fallSpeed)`, `Jumped`,
  `WallJumped`, `SlideStarted`, `Impacted(lostSpeed)` and exposes `GroundSpeed`, `Motion`,
  `LastRideInput`, `IsViewing` — so nothing in it can move the player, and it mutes and hides
  itself whenever another camera is on screen. Intensity is `Excitement`: speed against what is
  ordinary *for the current mount* (foot 4.8→9, bike 9→18, skis 9→22 m/s). **All audio is
  synthesised at startup** (`Audio/SfxSynth`: shaped noise → `AudioStreamWav`, loops crossfaded
  so the seam does not click) — the project has no audio files; replace any property with a
  sample to upgrade one sound. **No wind loop**: a synthesised one was tried and removed at the
  user's request — shaped noise reads as hiss, not air; wind needs a real recording. Shake goes through `Camera3D.HOffset/VOffset` (trauma², decaying),
  which no camera placement code writes, so it never fights the rigs. Speed lines are
  `shaders/speed_lines.gdshader` on a CanvasLayer at 4. Settings → Feel: volume, shake, speed lines.
- **Game / Sim profile** (`GameSettings.RideProfile`, Settings → Movement, `--profile game|sim`,
  default Game; `Rideable.Arcade`). Game is an arcade layer on the SAME equations: bike 350/900 W,
  0.88 rad lean, harder brakes; skis deeper edges, half the carve scrub, faster skating; running
  5.8 m/s. Sim is the untouched real-world model, the only one where `Bicycle.RiderWatts` (the
  home-trainer input) means anything. `--ride` forces Sim unless `--profile` is given, so its
  reference numbers (180 W → 32.7 km/h) stay checkable.
- **Tricks, landings, boost** (mounted, `FootPlayer`): hold **Trick (F / RB)** in the air and the
  stick flips (`_airPitch`) and spins (`_airSpin`) the rider+machine VISUAL — the body keeps its
  heading. Released, leftover rotation eases to the nearest whole turn. `GradeLanding` (air > 0.3 s)
  grades the residual angle: < 0.5 rad clean (named trick, speed kick, boost), < 1.1 sloppy (−45%
  speed), else bail (stopped, 1.2 s on the ground). **Boost (Q / LB)**, Game only: +7 m/s² while the
  meter lasts (0.4/s); filled by clean air and tricks. `Announced(text, good)` drives the popup +
  chime in `PlayerFeel`. The visual is rotated about a pivot 0.9 m up, not its origin at the
  contact patch, or a flip swings the bike through the ground.
- **Flying** (`Player/Flight.cs`, meshes in `Avatar/AircraftMeshBuilder`): a `Flyer` is a
  `Rideable` whose `Step` is unused — it owns a full 3D velocity and attitude (`FlightMotion`),
  because a ground vehicle is a speed along a heading and none of climbing, diving or banking fits
  that. `FootPlayer.FlyPhysics` carries the velocity through `MoveAndSlide`, turns anything the
  world took off past `CrashSpeed` into a crash (on foot, dazed 1.5 s — not a respawn), and poses
  the visual from the attitude about `Flyer.Pivot` while the capsule stays upright and yaw-only.
  `RideKind` 3–7 appended (never reordered: replicated as an int).
  - **Base jump**: not a mount. On foot, Jump while falling (vy < −3) with > 12 m under you →
    **wingsuit** (lift/drag polar, point mass; a fall pulls out into a glide on its own). A bare
    polar porpoises for ever (measured −36 m/s dive → 13:1 zoom → repeat), so sink is damped toward
    the polar's steady glide: settles at **133 km/h, 2.7:1, 13 m/s sink**. Jump again → **parachute**
    (glide 2.1, 4.2 m/s sink, opening shock from 145 to 36 km/h in 1 s); touching ground → on foot.
    Wingsuit touching ground over 12 m/s = SPLAT. Proximity (< 20 m AGL at > 30 m/s) is scored.
  - **Paraglider** (picker): the same `Canopy` model at 9.1:1 / 38 km/h; on the ground push forward
    to run, Jump to launch; stays worn after landing.
  - **Helicopter** (picker): the look sets the heading (`LookSteers`, mouse/right stick turn
    `_viewYaw`, not `_lookYaw`), stick flies, Space/RT up, Ctrl/LT down, release holds altitude.
  - **Plane** (picker): throttle is a LEVER (Shift/RT up, Ctrl/LT down) — nobody holds a key for a
    whole flight. Stick pitches/rolls, heading follows bank (coordinated turn), roll AND pitch
    self-level hands-off. Thrust 4.5 m/s² — at 11 it beat gravity and a pull-up climbed vertically
    for ever. **Airspeed is carried as state** (`FlightMotion.Airspeed`): re-deriving it as
    velocity·nose fed the stall sink back in as speed once the nose dropped (112 → 394 km/h in 2 s).
  - Check any of them: `<godot> --path . -- --flycheck wingsuit|glide|paraglider|heli|plane[,out.png]
    [--at E,N]` — scripted sortie with the real input actions, speed/sink/glide/AGL every second,
    non-zero exit on a crash or ending under the terrain. `FootPlayer.DebugLaunch` puts a craft in
    the air for it (no runway or launch slope needed to test a flight model).
  - Sound: synthesised helicopter rotor (4.5 Hz blade "whop") and piston engine loops, pitch by
    spool/throttle. No wingsuit/canopy wind — see the removed wind loop above.
- **Mantle** (on foot): pushing into a wall whose top is 0.45–2.1 m above the feet, with open air
  over it and standing room on it, pulls you up (automatic in the air, needs Jump on the ground so
  walking into garden walls does not vault them). Jump + mantle therefore reaches ~3 m. Moved
  directly, not through MoveAndSlide, which exists to stop exactly this contact. Ground coyote
  time 0.12 s. Check: `<godot> --path . -- --mantlecheck` (1.4 m and 2.8 m must climb, 3.6 m not).
- **Steering by lean** (`Rideable.SteerByLean`): the input sets a target bank that eases in over
  ~0.2 s (out 1.6x faster) and the yaw rate is what that bank sustains, `g·tanφ/v`. Setting the yaw
  rate straight from the input made every correction a jerk — the "stiff" feel. Ski edge scrub is
  quadratic in bank, so a moderate carve holds speed. The chase camera trails the turn
  (`_turnLag` ∝ yaw rate) instead of being bolted behind the rider.
- **On foot** (`src/Player/FootPlayer.cs`): WASD + Shift at 1.6 / 4.6 m/s, Space to jump, plus
  two momentum moves — **slide** (Ctrl, run only, launches at 7 m/s, gains speed downhill, ends
  keeping horizontal speed if you Space out of it) and **wall jump** (Space in the air against
  a surface past ~70°, twice per airtime, never twice on the same face). Both launches decay
  back to `RunSpeed` through `AirDrag`, so neither raises the top speed on flat ground; the
  air branch *steers without braking* above running pace, because the ordinary `MoveToward`
  air control kills a launch in half a second and makes both moves pointless. Sliding shrinks
  the capsule to 0.9 m, so it fits where standing does not.
- **Mounts** (`src/Player/Rideable.cs`): **E** opens a picker (`RideUi`) — On foot / Road bike /
  Skis. A vehicle is a table of numbers plus a mesh: everything touching the body, the network,
  the camera and the UI lives once in `FootPlayer`, so adding one is a class plus a line in
  `Rideable.Create`. Both share one model — mass, a resistive force, `SlopeAccel` — and differ
  only in where propulsion comes from. Mounted, speed is a **scalar along a heading**, not a
  velocity vector: a bike goes where it points, and strafing is something people do, not
  vehicles. The camera goes third-person with a raycast pull-in, and the machine's lean is
  *derived* (`tan φ = v·ω/g`), never authored.
  - `Bicycle` runs the real power equation, `m·a = P/v − ½ρ·CdA·v² − Crr·m·g − m·g·sinθ`.
    Nothing is tuned: 180 W gives 32.7 km/h flat, 9.3 km/h up 8%, and 63.8 km/h freewheeling
    down it. Steering is lean-limited, so the turn radius grows with speed. `RiderWatts` is the
    input **because a home trainer measures watts** — RideLink drops straight into it.
  - `Skis` have no engine. Turning *costs* speed (`EdgeScrub`), which is the whole of skiing:
    pointed straight down a 30% face you reach 80 km/h, and carving across the fall line is the
    only brake. W is a capped poling shuffle, because skis on the flat would otherwise strand you.
  - `FootPlayer.RideControls` replaces the keyboard when set — one movement path for a keyboard
    rider and a pedalling one, and the seam `RideProbe` and the trainer both use.
  - `RideKindId` is replicated, so remote players are seen on the bike rather than sprinting
    at 40 km/h in a running pose.
  - **Space hops** on either mount (`FootPlayer.RideJumpVelocity`, 3.2 m/s, edge-triggered, ground
    only): a bunny hop or a pop off a lip that carries the momentum it already had. The free-fly
    camera uses the same keys vertically — **Space** up, **Shift** down (Q/E still work), boost
    moved to **Ctrl**.

## Commands

- Riding check: `<godot> --path . -- --ride bike|skis,seconds[,out.png] [--at E,N]` — mounts,
  holds the throttle via `RideControls`, and prints speed/altitude/clearance every 2 s with a
  non-zero exit if the rider went nowhere or ended under the terrain. Riding is the one part
  that cannot be judged from a screenshot; add `--ridemenu` (with `--shot`) to capture the picker.

## Gotchas

- **Player scale is set by speed, not by size.** A 1.8 m capsule moving at 6-14 m/s reads
  as a giant next to 10 m buildings. Realistic 1.6 / 4.6 m/s plus head bob and a running
  FOV kick is what makes the world feel human-sized. `FootPlayer` reads *physical keys*,
  so `Input.action_press` will not drive it in tests — use godot-ai `game_manage input_key`.
- **`IsOnWall()` flickers between adjacent physics frames.** Pressed flat against a building
  face, the solver reports contact on roughly every *other* frame, so a wall jump gated on
  same-frame contact silently misses about half of all attempts — it looks like an input bug,
  not a physics one. `FootPlayer` remembers the last qualifying wall normal for 0.18 s
  (`WallCoyoteTime`) and the last jump press for 0.14 s (`JumpBufferTime`), and jumps when
  both are live. Verified: v.y = 4.6 and 5.41 m/s along the wall normal, one frame after press.
- **A held movement key that starts a state must be edge-triggered.** A spent slide ends at
  ~2 m/s, the walk puts you back over the 2.6 m/s entry threshold in about a second, and a
  *held* Ctrl then starts the next one — measured as a permanent 7 m/s crouch-run. Slide entry
  takes a fresh press; holding only sustains the slide you are in.
- **Feeding collision back into a vehicle needs `GetRealVelocity`, and a threshold.** Two wrong
  versions came first. (1) `Velocity` after `MoveAndSlide` is *projected along whatever you hit*,
  and against a slope too steep to climb that projection points up the face and keeps most of its
  magnitude — a skier jammed against a bank reported 22 km/h while its position had not changed
  for twelve seconds. (2) Clamping to `GetRealVelocity` every frame then killed the bike, because
  the ground is a 2 m lattice and crossing each bump costs a little forward motion *every frame*;
  compounded, that bled a bike from 107 m of riding to 11 m on flat ground. Only a shortfall
  that **persists** (smoothed, and past `ImpactTolerance`) is an impact.
- **Crouch states need a headroom test before standing.** `FootPlayer.EndSlide` returns false
  when a standing capsule will not fit (shape query, radius shaved 3 cm), so releasing Ctrl in
  a tunnel keeps you down instead of forcing the body up through the roof — and you cannot
  jump out of a slide you could not stand up in either.
- **Cars and drifting** (`Player/Car.cs`, `CarSpec`, `RideKind` 8–10: Coupe 86, Rotary FD, Rally 4WD;
  issue #1). A car is the one mount that does not go where it points, so `RideMotion` gained **`Slip`**
  (travel minus nose, rad, + = left; π reversing) and `FootPlayer.RidePhysics` moves the body along
  `Yaw + Slip` — zero for every other mount, which is why nothing else changed. The model is a planar
  bicycle model in `RideMotion` alone (speed, slip, yaw rate), so a wall, boost or a sloppy landing that
  edits `Speed` applies to the car too: slip angles through `sin(C·atan(B·α))` (peak ~0.15 rad), each
  axle's side force limited to what its **friction circle** leaves after drive/brake force, load
  transfer from the last step's acceleration, 5-speed auto box, 4 substeps. Every way into a drift
  falls out of that: **handbrake** (Space / A — `Rideable.CanHop` false, `RideInput.Handbrake`) collapses
  the rear circle, power-over eats it, and braking into a turn unloads the rear (feint). Game adds grip,
  power, a counter-steer assist and a **yaw moment that catches the car past ~35°** (the fronts are on
  the lock stop by then, so steering alone cannot); Sim has none of it. Two traps found by the check:
  the low-speed kinematic blend must key on TOTAL speed (keyed on forward speed it zeroed the sideways
  speed at 70° of angle, 50 km/h gone in 0.3 s), and speed-scaled steering lock must lift in a slide or
  there is not enough counter-steer to catch anything. The chase camera swings ~55% toward the travel.
  Known limits: the body is still the player capsule (radius 0.85 m), and there is no per-surface grip,
  so the 4WD does not yet get its gravel advantage. Check: `<godot> --headless --path . -- --driftcheck
  [--trace]` — flat ground, no world: launch, handbrake entry, 4 s hold, recovery for every car in both
  profiles; non-zero exit on a spin, no drift, or no recovery.
- **What others see is what the owner sees** (`FootPlayer` pose sync, issue #9). A remote copy
  used to get position, yaw and ride kind only, so nobody else ever saw a lean, a trick, a bail, a
  craft's attitude (it was posed level), a turning rotor or crank, a slide, a jump or a stunned
  body, and every peer ran its own gait phase. Three more synced properties now carry it:
  **`BodyPose`** (the visual's local transform — lean, bank, flips/spins, bails, flight attitude,
  landing squash, stun all in one value, applied as-is), **`PoseKind`** (stride / air / tucked) and
  **`Anim`** (a `Vector4`: on foot speed + gait phase; mounted whatever the `Rideable` writes in
  `WritePose` and reads in `AnimateRemote` — bike cadence + crank angle, craft spool + throttle).
  Owner and remote draw the on-foot figure through the same `ApplyFootPose`; a fresh gait phase or
  crank angle is taken as-is and only integrated between updates. **A new mount with moving parts
  plugs into `WritePose`/`AnimateRemote` and never touches the sync code** — the drift cars'
  slip, steer angle, wheel spin and rpm go there. The pose is reset on every ride change (see the
  gotcha). Remote helicopters and planes are heard (spatial `EngineSynth` from `Anim`), and a
  parked craft's rotor follows the synced `VehicleBody.Spool` instead of a guess from `EngineOn`.
  Check: `<godot> --path . -- --synccheck [--at E,N]` — an owner runs walk, sprint, jump, slide, a
  leaning bike ride, a helicopter climb and a rolling plane while a MIRROR (foreign authority, so
  it takes the remote path) is fed the owner's real `ReplicationConfig` properties at 20 Hz of
  wall time; non-zero exit if the mirror differs the frame after an update (must be 0: that is
  state not replicated) or drifts between updates past one interval. Measured: 0.0000 fresh on
  pose, hand and crank; with the old replication set, plane attitude off by 3.0, crank 3 rad,
  bike lean 0.6, hand 0.64 m. One process, no sockets: it tests that the state is complete and
  both sides derive the same picture, not ENet.
- **Hitboxes are measured, never typed** (`Avatar/MeshBounds`, `Player/Hurtbox`). Movement keeps
  its capsule (a rigid 11 m box would snag every slope of the 1 m lattice), but every drawn
  machine — mounted player or parked `VehicleBody` — also carries a **`Hurtbox`**: an `Area3D`
  fitted to its mesh bounds, parented to the VISUAL so it banks and flips with it, alone on
  physics layer 8 (`Hurtbox.Layer`), monitorable, not monitoring — no movement changes. A shot that
  wants it sets `CollideWithAreas = true` with `Hurtbox.Layer` in its mask and resolves the hit
  with `Hurtbox.BodyOf` (combat, PR #6, needs that one-line change to hit wings and rotors).
  `Rideable.ParkedBox` defaults to the parked mesh's bounds (`Measured`, once per kind; the
  helicopter leaves its "Rotor" out; the plane keeps a documented fuselage-only box, checked to lie
  inside its mesh), and traffic units take their mesh's own AABB. Before: bike parked box 1.10 m
  tall for a 0.91 m bike, traffic car/van boxes 15/20 cm over the roof, carriages 30 cm short.
  Check: `<godot> --path . -- --hitboxcheck [--at E,N]` — per mount: drawn bounds, capsule, parked
  box, hurtbox; live rays through a wingtip and a rotor rim; and with `--at` in a town, a ray from
  outside at up to 5,000 faces of the real `.bldg` tiles through `ChunkNode.BuildingShape` (the
  building collision is one-sided `ConcavePolygonShape3D` with the render's raw winding — this is
  the raycast verification the bridge-deck gotcha asked for; not yet run on a region with
  buildings).
- **A replicated value whose MEANING depends on another replicated value must be reset when that
  one changes.** `Anim` means gait speed + phase on foot and cadence + crank angle on a bike; the
  ride kind and `Anim` arrive in the same update, but the owner had not rewritten `Anim` yet on the
  frame the ride changed, so the bike read the rider's stride phase as a crank angle (0.93 rad, an
  instant snap). `ApplyRide` zeroes `Anim`/`BodyPose`/`PoseKind` with the kind.
- **Simulate a network's rate in wall time, not frames.** `--synccheck` first copied every third
  frame; at WSL's 33 fps that is 11 Hz, and a plane rolling at 2.2 rad/s legitimately drifted
  0.26 rad between updates — a failure that was the probe's, not the sync's.
- **Initial D roster, real specs, racing** (`CarCatalog`, `RaceLine`, `DriveProbe`; #5). 26 cars, append-only,
  `RideKind` = 8 + index (8..63 reserved for cars). Each `CarSpec` carries the real car: crank torque curve
  (`Torque`, interpolated by `TorqueAt`), OEM `Tyre` (rolling radius from the sidewall), published braking
  (`BrakeDecel`), `Differential` (an open diff stops pushing at 72% of the driven axle's grip, so it will not
  power over like an LSD car), `Style` Drift/Grip as in the series, and published `RefZeroTo100`/`RefTopKmh`
  that `--driftcheck` compares the model against (all within ±15% / ±8%; figures are from recall, not
  source-checked). Game: every car must hold a drift (the throttle keeps the rear sliding once sideways,
  more for FF); Sim: grip cars need not slide.
  **`--drivecheck --cars 0,1,3,4,12,6`** races them all at once, colliding, down the main road from the
  spawn: a minimum-curvature `RaceLine` inside the tarmac (curvature over ±8 m AND ±4 m — RoadGen's
  junction gaps leave 15-20° kinks the wide window hid), a per-car quasi-steady speed profile (corner
  `√(μg/κ)`, crest `√(gR)`, forward power/traction pass, backward braking pass), drift planning only for
  Drift cars by stepping `Car.Clone()` through a handbrake entry (committed only if the sim stays 1.5 m
  inside the edge), soft-hands catching of unplanned slides, reverse-out when stuck, passes on straights.
  `--record prefix` writes a GPX per car with the nose yaw (`<us:yaw>`); the GPX replay plays it as a car
  (`<type>car:N</type>`, `Runner.KeepOutside` keeps every cinema lens out of the body) — that is how a race
  is shown in Absolute Cinema. Traps found: the brake at a standstill selects REVERSE (the grid held the
  brake and reversed off the line — hold the handbrake); a reversing car has 180° of slip and is not a slide.
- **AutoPilot and RaceRoute** (`Player/AutoPilot.cs`, `Player/RaceRoute.cs`): the scripted racing driver
  and its road, shared by `--drivecheck` (all cars in one scene) and multiplayer races (`--raceauto`,
  see `src/World/CLAUDE.md`). `RaceRoute.BuildAsync` walks the main road from a point over the `.road`
  tiles (bridging RoadGen's trimmed junctions within 18 m), `FromPoints` rebuilds one a server sent.
  `AutoPilot.Drive(dt, go, others)` plugs into `FootPlayer.RideControls`; `others` are the other cars
  (position, speed, wreck?) for racecraft.
- **Tyre wear, brake wear and fade** (Settings -> Feel, off by default; `--tyrewear on`,
  `--brakewear on`; #20). Tyres wear per axle with sliding work (side force x slip speed + wheelspin,
  ~25 MJ per axle) and lose up to 30% of peak grip when finished — a drift burns the rears, grip driving
  barely scrubs. Discs heat with braking power (capacity 9 J/K per kg of car), cool faster with airflow,
  fade past 450 C (braking down to 40% at worst), and pads wear with the energy put through them. The
  car HUD shows tyres % and disc temperature. `--driftcheck` checks both: 30 s of drift wears the rears
  6%, fronts 1.6%; 15 stops 150->50 km/h take an AE86's discs to ~570 C (braking at 65%), a minute of
  cruising cools them to ~110 C.


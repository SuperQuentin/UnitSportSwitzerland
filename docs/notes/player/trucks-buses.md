# Trucks and buses (#70)

- **Roster** (`Player/HeavyCatalog`, `RideKind` **96..119**, append-only; `RideKind.Trailer` = 120 is a
  lone trailer in the world, never mounted): Scania R 450 tractor (Swiss Post yellow), MAN TGS
  26.440 6x2 rigid with swap body (Migros), Mercedes Citaro 12 m (VBZ), Citaro G 18 m articulated
  pusher (Bernmobil), Setra S 516 HD coach (PostAuto). Operators' colours only, no names or logos.
  **Trailers** (`TrailerCatalog`, index append-only): curtainsider 13.6 m, fuel tanker (sloshes),
  timber (the logs are the load), drawbar trailer (dolly + swap body, two pivots). A trailer's
  **code** = `(index+1) | load% << 8`, replicated as `FootPlayer.TrailerCode`. Figures are the
  published ones where they exist; every entry comments what is assumed (CG heights, mass splits,
  retarders, the Setra's box, the Citaro G's joint position).
- **Physics** (`Player/HeavyTrain`): each section (tractor, trailer, dolly, bus half) is a planar
  rigid body with mass, yaw inertia and its own axles; every axle makes its own force from its
  own slip angle (`sin(C·atan(B·α))`, B 8, a truck tyre's shallow slope), friction circle, EBS
  share of the brakes and ABS limit. Pins (fifth wheel, drawbar, turntable, bus joint) are
  velocity constraints solved by sequential impulses (2x2 per pin, 12 iterations, Baumgarte 0.2)
  plus a one-sided articulation stop. Static axle loads from the tail forward (a fifth wheel or
  turntable carries the front of the section behind; a drawbar carries nothing); longitudinal
  load transfer per section. Off-tracking, jackknife, a reversing trailer running away and
  tri-axle scrub are not scripted. The first section is the player's body, driven through
  `RideMotion` like a car; the others are placed from `Truck.Articulation` each step (the frame
  is rebuilt from the angles, so nothing drifts off a pin).
- **Collision**: every section behind the cab is its own `CharacterBody3D` (`FootPlayer.Heavy.cs`,
  `TopLevel` children named `Section{k}`, two hull boxes measured from that section's mesh, lift
  0.6 m), moved by `MoveAndSlide` toward where the train says it is; what stops it becomes a
  `TrainContact` solved with the pins next step (a trailer jammed on a bollard holds the tractor
  back), and a section held more than 0.25 m off its line takes the angle it is really at. Built
  on remote copies too, posed from `TrainPose`, so others hit what they see. The cab and every
  section pitch to the ground under their axles and pin (a 12 m bus on a 10% road: its level
  hull would dig into the slope).
- **Driveline** (`Player/HeavyDriveline`): the engine is a flywheel of its own (published torque
  curve, idle governor, friction, exhaust brake) against a dry clutch or a torque converter
  (pump ∝ ω², ratio 2 at stall, lock-up). Five modes (Settings → "Truck gearbox",
  `--gearbox auto|seq|seqclutch|hsplit|h`): **Automatic** (AMT: start gear from load and grade,
  predicts the speed lost in the half-second shift and holds the gear rather than hunt on a hill,
  skip-shifts), **Sequential** (auto clutch, never stalls), **Sequential + clutch** (shift only
  with the pedal down, else it grinds; it stalls), **H-pattern + splitter** (gates 1–6 on the
  number keys, ` reverse, 0 neutral, splitter on the shift keys, engaged when the clutch is down
  or the throttle lifts), **H-pattern** (the splitter changes by itself). City buses (converter)
  take Automatic or Sequential only; the 8-speed coach has no H-pattern. **Retarder** stalk
  (`'` / `;`): 1 exhaust brake, 2–4 retarder thirds; the automatic blends it into the first third
  of the brake pedal. **Air**: chamber pressure lags the pedal (0.28 s on, 0.45 s off), each
  application draws the tank, the compressor refills it; under 4.5 bar the spring brakes come on.
  Space holds the spring brakes. The automatic backs up on a press of the brake once the truck is at rest with the pedal let go (never on a brake held down to the stop), and a stopped truck holds itself on its brakes (hill hold) until the throttle goes down; with the clutch pedal both are the driver's job. Game: brakes instant, tank never drains, +15% grip, ABS at 80%,
  the retarder limited to 45% of the drive axle's grip, stretch braking and a fold damper on a
  folding pivot, a yaw assist; rollover threshold ×1.6. Sim has none of it.
- **Rollover**: each section's steady lateral acceleration (speed × yaw rate, lagged 0.35 s as a
  body rolls onto its springs; a tank's slosh shifts its CG) against its static threshold
  `track/2 / h_cg · 0.78` (full tanker 0.42 g, loaded swap body 0.40 g, city bus 0.68 g); past it
  for 0.3 s the vehicle is wrecked ("ROLLED OVER!"). A drawbar trailer amplifies the tractor's
  lateral acceleration (rearward amplification): yanking the wheel with one loaded rolls it.
- **Coupling** (`H` / D-pad ←): a tractor backs its fifth wheel under a lone trailer's kingpin
  (within 0.9 m, yaw within ~50°; a drawbar eye within 0.9 m, ~70°), the trailer is claimed
  through `VehicleManager` like getting into a vehicle. `H` again drops it where it stands (parked
  with its own angles). The picker's **Trailers** fold couples one at once behind a stopped truck
  that takes it, or leaves it 14 m ahead. Getting out parks the whole train (`VehicleState.Train`, stood on the slope as it was driven by `HeavyGround`;
  `Angles`, `Flags`, `Load`; the server counts it as two vehicles for `MayPark`).
- **Buses**: `G` doors (all at once, stopped; a city bus kneels with them), `K` kneel, `N`
  destination (Label3D on the front, `HeavyLook.Destinations`). Door leaves swing out of real
  holes in the right wall. Doors shut and the bus rises when it pulls away.
- **Replication**: `Anim` (steer, wheel spin rate, rpm, lamps / reverse / kneel / doors /
  destination bits), `TrainPose` (joint angles, with the pose properties, eased 15/s on the
  mirror), `TrailerCode` (on change). The cab's terrain pitch travels in `BodyPose`.
- **Camera**: behind and above the whole train (`5 + 0.95·length` m back), swinging round with
  the trailer's angle (`Truck.ChaseSwing`); the pull-in ray ignores the train. First person hides
  the cab's shell (its glass boxes block the view from inside; a cockpit is #69).
- **Measured** (`--truckcheck`, Sim, loaded, flat): Scania + curtainsider 39 t 0-80 in 50 s, top
  86 km/h (limiter 89), 80-0 in 41 m, 12% start to 20 km/h; MAN road train 40 t 0-80 in 57 s,
  12% to 18 km/h in A6; Citaro 0-50 in 14 s, Citaro G 17 s, coach 9.6 s. Off-tracking at R 11.5 m:
  trailer axles on 7.6 m against 7.7 m from the chain. Swept width at a 12.5 m outer radius 7.19 m
  (the EU turning-circle rule allows 7.2). Empty trailer, snow, retarder full: Sim folds to 82°,
  Game holds it to 10°. Full tanker at 70 km/h in a 0.5 g bend: the tanker goes over first.
- **Checks**: `--truckcheck [trace]` (above, non-zero on a miss); `--truckprobe N[,s[,shot]]
  [--trailer M] [--kmh V] [--load x] [--minor] [--trace] --at E,N` drives the real road from the
  spawn (Road class and wider unless `--minor`), prints off-tracking against the centreline,
  section hits and a road-width report (the swept width at each bend against the TLM width);
  `--heavynet a|b [pw]` on two clients of a loopback server (a with `--admin-password`): the train,
  coupling, parking and the bus from the other peer; `--ride truck:N --trailer M [--steer x]`.
- **Not done**: shots on a trailer do not hurt the vehicle (`Hurtbox.BodyOf` stops at the section
  body); no AI drivers for trucks; the first-person cockpit (#69); a steering wheel's range
  (#68); routing by road width (the report is there, the router is not).

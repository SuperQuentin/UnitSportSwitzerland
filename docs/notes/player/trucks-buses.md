# Trucks and buses (#70)

- **Roster** (`Player/HeavyCatalog`, `RideKind` **96..119**, append-only; `RideKind.Trailer` = 120 is a
  lone trailer in the world, never mounted): Scania R 450 tractor (Swiss Post yellow), MAN TGS
  26.440 6x2 rigid with swap body (Migros), Mercedes Citaro 12 m (VBZ), Citaro G 18 m articulated
  pusher (Bernmobil), Setra S 516 HD coach (PostAuto); the Raptor pickup (101, #463); the farm
  machines (#494, `farming/machines`): Fendt 724 Vario tractor (102), Claas Lexion 6800 combine
  (103). Operators' colours only, no names or logos.
  **Trailers** (`TrailerCatalog`, index append-only): curtainsider 13.6 m, fuel tanker (sloshes),
  timber (the logs are the load), drawbar trailer (dolly + swap body, two pivots); boat trailers
  (4, 5, #463); the farm implements on `Coupling.ThreePoint` (rigid, no wheels: plough 6, drill 7,
  mower 8) and the tipping trailer (9, drawbar, its sacks in the code's farm bits). A trailer's
  **code** = `(index+1) | load% << 8`, replicated as `FootPlayer.TrailerCode`. Figures are the
  published ones where they exist; every entry comments what is assumed (CG heights, mass splits,
  retarders, the Setra's box, the Citaro G's joint position).
- **Numbering from 101 on** (#613): `HeavyCatalog` numbers its first five entries by position;
  every later one names its own kind (`Kind = (RideKind)N`). Branches appending at the same time
  then keep their numbers: 101 the F-150 (#470), 102 and 103 the farm tractor and the combine
  (#541), 104 the tipper and 105 the mixer (#613). A clash, or an unnamed sixth entry, throws at
  start-up. `HeavyCatalog.For` looks up by kind, not by index.
- **Site lorries** (#613, epic #605), `HeavySpec.Body` (`TruckBody.Box | Tipper | Mixer`):
  - **104, Arocs 3245 8x4 tipper**: two steered front axles; its body tips about a hinge low at the
    back, the rig's tipping node from #677 (`HeavyParts.Tip`, 50°).
    - The tailgate (`HeavyParts.Tailgate`) hangs plumb from its top hinge as the body rises, so it
      swings open.
    - The load is a gravel heap in the body, as high as the truck is loaded. Tipping does not empty
      the truck (its mass stays).
  - **105, Arocs 3240 8x4 mixer**: its drum (`HeavyParts.Drum`, axis pitched up 0.2 rad toward the
    back, stripes so its turning shows) turns while the engine runs. That is 2 to 12 rpm with the
    engine's speed (`Truck.DrumRate`), backwards and faster while it discharges, when its chute
    (`HeavyParts.Chute`) swings out.
  - Liveries: invented contractors, colours only (Gruber Bau AG red, Betonwerk Aare white and
    blue). Not hashed per truck yet.
- **The work bit**: both work on `Truck.Tipped` (`TipBit`, from #677) and the bus's
  **destination** action (N; the destination dash poke in VR, `XrCabControls` "-work"), stopped.
  The mixer's discharge is the same bit (`Truck.Discharging`).
  - Moving, it refuses ("Stop to tip the body"). Pulling away drops the body and stops the
    discharge, as it shuts a bus's doors.
  - It rides in the pose's door bits (`Truck.TippedInPose`) and parks with the truck.
  - A mixer's pose carries -1 for its rpm with the engine off, so a copy's drum stops too.
- **On the building sites** (#616's provider): the plan's tipper parks as 104, its mixer as 105,
  each standing axle by axle.
- **A held brake reverses a stopped truck** (an automatic box's shuttle): a check waiting at a
  standstill lets go of the controls rather than braking. A braked wait backed the tipper away at
  2 m/s and its tip was refused.
- **Checks**:
  - `--tippercheck tipper|mixer[,shots] --world flat --systems physics,ui` (`TipperCheck`), on the
    real binding. Tipper: driven; tipped in 3 s with the tailgate open; down again; refused at
    15 km/h; dropped by pulling away; kept up in the flags. Mixer: driven; the drum turning with
    the engine and stopped without it; discharging backwards and faster with the chute out; back.
    Shots go in `test_output/tipper/`.
  - A lorry round in `tools/excavatornetcheck.sh`: B sees the tipper's body up, the mixer's drum
    backwards with its chute out, then the drum stopped with A's engine.
- **Physics** (`Player/HeavyTrain`): each section (tractor, trailer, dolly, bus half) is a planar
  rigid body with mass, yaw inertia and its own axles; every axle makes its own force from its
  own slip angle (`sin(C·atan(B·α))`, B 8, a truck tyre's shallow slope), friction circle, EBS
  share of the brakes and ABS limit. Pins (fifth wheel, drawbar, turntable, bus joint, a
  three-point linkage welded rigid, #494) are
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
- **Pickup and boat trailers** (#463): the **Ford F-150 Raptor** (`HeavyCatalog` 101,
  `HeavyClass.Pickup`, `PickupMeshBuilder`: SuperCrew cab with the heavy cockpit, 5.5 ft bed, five
  seats) runs on the same train model: 10R80 converter automatic (`StallRpm` 2600), a petrol V6's
  `EngineInertia` 0.3 (a truck diesel's is 3.5), hydraulic brakes (`AirBrakes = false`: no chamber
  lag, no tank, no hiss), 4x4, fords 0.8 m. Its hitch is `Coupling.Ball` (0.55 m up, 0.12 m behind
  the bumper), which **carries** like a fifth wheel (`HeavyTrain.Carries`): the nose weight is on
  the ball. `HeavySpec.Accepts`: a ball trailer also hangs on the rigid's drawbar jaw (a
  combination coupling's ball); the tractor takes none.
  - **Doors** are a car's (`Truck.CarDoors`): four `HeavyDoorLeaf`s hinged at their front edge
    (`HeavyParts.CarDoors`), worked one by one through `Avatar.IHingedDoors` (now `CarRig` and
    `HeavyRig`; `VehicleBody.Doors`): G at a door, E opens then gets in, a VR hand grips it, the
    server passes it on (`RequestDoor`). Bit 2 is the driver's (front left), as `CarRig.DriverDoor`:
    it opens getting in and out and shuts behind. Replicated driving in `Anim`'s door bits, parked
    in `VehicleBody.DoorsOpen`.
  - **Collision** (`Truck.HullBoxes`): the body up to the bonnet and bed rails, above that only the
    cab (measured, one box ran to the roof over the bonnet and bed).
  - **Boat trailers** (`TrailerCatalog` 4 jetski, 5 speedboat; `TrailerBody.Boat`,
    `TrailerSpec.Boat/BoatAt/BoatKeel`). **The boat is a boat of its own**: a parked boat
    `VehicleBody` in the trailer's **cradle**, a hold (`vehicles/vehicles-in-holds`) that takes only
    that kind (`CargoBay.Only`) on a deck that is only a hold (`VehicleDeck.CargoOnly`: nothing to
    walk, `Rideable.Walkable` ignores it). `Truck.Decks` adds the trailer's cradle as the train's
    section; `ParkedTrailer.Decks` has it too, so the boat rides the driven train, the parked train
    and a dropped trailer, on every peer (a copy's driver is found from its `_remoteRide` truck with
    its trailer). Strapped, it is drawn level and dry, never got into (`VehicleManager.OnTrailer`).
    The trailer's code load says only the weight: boat aboard (100) or not (0). The menu preview
    draws the boat on it (`HeavyParts.Cargo`, `boatShown`). `SectionSpec.PivotHeight` is the
    coupler's level height: on the truck's higher jaw the trailer rides nose up
    (`HeavyGround.LevelPivot`). Measured: 7-10 % nose weight.
  - **Trap**: a carrier's ground rays hit its own cargo: the trailer stood itself on its boat's
    hull, 2.5 m up and pitched, and dragged on the slipway. A hooked boat is excluded from its
    carrier's rays (`FootPlayer.Cargo`, `VehicleBody.Cargo`); `GroundQuery` looks past any vehicle
    in a hold for the moments before it hooks (a dropped trailer standing on its boat lifted it out
    of the cradle, and it never found it).
  - **Launch / winch** (`{car_door}` with a boat trailer, stopped; `FootPlayer.Heavy` `ToggleBoat`):
    the boat's spot is behind the trailer, bow clear of its back (`BoatSpot`); water there at least
    0.55 of the hull's depth (about its draft; `WaterField` level minus the terrain) launches it: the
    strapped boat is claimed and parked afloat there. With the trailer empty, a free boat of its kind
    within 6 m of that spot is claimed and parked on the bunks (`OnBunks`). Claim + park keep the
    server's count (`VehicleState.Units`) even, so anyone may, not only an admin. The picker's trailer
    with load half or more parks its boat on it too (admin). The HUD hint says when either works.
  - **Controls** on every device (`general/new-action-three-devices`): couple H / D-pad ← / VR right
    stick ← (with #436, `XrPad`); launch / winch G / X / VR X; doors G / X / VR grip, E to get in; the
    hints come from `InputHints`. VR target (`xr/vr-action-map`): latch the coupler and crank the
    winch by hand.
  - Not done: no slipway driving physics (the trailer's wheels in water are ordinary ground), the
    boat appears at its spot rather than sliding off; a sunk trailer is not wrecked; no two-client run.
- **Walking in a bus** (#162): its deck, the joint's passage and hollow bellows, see `walk-aboard`.
- **Buses**: `G` doors (all at once, stopped; a city bus kneels with them), `K` kneel, `N`
  destination (Label3D on the front, `HeavyLook.Destinations`). Door leaves swing out of real
  holes in the right wall. Doors shut and the bus rises when it pulls away.
- **Replication**: `Anim` (steer, wheel spin rate, rpm, lamps / reverse / kneel / doors /
  destination / throttle-eighths bits), `TrainPose` (joint angles, with the pose properties, eased 15/s on the
  mirror), `TrailerCode` (on change). The cab's terrain pitch travels in `BodyPose`.
- **Camera**: behind and above the whole train (`5 + 0.95·length` m back), swinging round with
  the trailer's angle (`Truck.ChaseSwing`); the pull-in ray ignores the train. First person is the
  cockpit (#157, `cockpit`, `heavy-cabin`): hollow cab, glass panes, the driver at the wheel.
- **Measured** (`--truckcheck`, Sim, loaded, flat): Scania + curtainsider 39 t 0-80 in 50 s, top
  86 km/h (limiter 89), 80-0 in 41 m, 12% start to 20 km/h; MAN road train 40 t 0-80 in 57 s,
  12% to 18 km/h in A6; Citaro 0-50 in 14 s, Citaro G 17 s, coach 9.6 s. Off-tracking at R 11.5 m:
  trailer axles on 7.6 m against 7.7 m from the chain. Swept width at a 12.5 m outer radius 7.19 m
  (the EU turning-circle rule allows 7.2). Empty trailer, snow, retarder full: Sim folds to 82°,
  Game holds it to 10°. Full tanker at 70 km/h in a 0.5 g bend: the tanker goes over first.
- **Checks**: `--truckcheck [trace]` (above, non-zero on a miss; the pickup's 0-100, which hitch takes
  which trailer, nose weight, the cradles, the Raptor's doors, the units: `BoatTrailers`);
  `--boattrailercheck jetski|speedboat[,shots] --chunks fixture:lake --traffic 0`: the slipway, launch,
  winch, the boat on the bunks within 5 cm, the parked train, the doors, a dropped and re-coupled trailer; `--truckprobe N[,s[,shot]]
  [--trailer M] [--kmh V] [--load x] [--minor] [--trace] --at E,N` drives the real road from the
  spawn (Road class and wider unless `--minor`), prints off-tracking against the centreline,
  section hits and a road-width report (the swept width at each bend against the TLM width);
  `--heavynet a|b [pw]` on two clients of a loopback server (a with `--admin-password`): the train,
  coupling, parking and the bus from the other peer; `--ride truck:N --trailer M [--steer x]`.
- **Not done**: shots on a trailer do not hurt the vehicle (`Hurtbox.BodyOf` stops at the section
  body); no AI drivers for trucks; a steering wheel's range
  (#68); routing by road width (the report is there, the router is not).

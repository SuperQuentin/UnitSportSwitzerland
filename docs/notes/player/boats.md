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
  (banked, it pulls the boat round), **trim** as the hull's own wave tilting the water it floats on
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
- **What others see** (`what-others-see-what-owner`): the owner sends `Anim = (rpm, thrust share,
  Heave, wet + 2·airborne)`. **`Heave` = the body's height over the mean surface under the hull's
  centreline** (`Boat.TrySurface`, three points). A remote copy (`FootPlayer._Process`,
  `Boat.RemoteY`) and a parked copy (`VehicleBody.DrawBoat`) draw it at that height over **their own
  copy of the waves at their own time**, so however far behind the position stream is, the boat sits
  on the waves that peer draws; attitude comes as is (`BodyPose`, `Tilt` eased). Measured on
  loopback (`tools/boatnetcheck.sh`, gamey): the copy's pitch against B's surface slope correlates
  0.98 / 0.90 / 0.99 (idle / running / parked) where A's own boat does 1.00 / 0.93 / 0.99 on A's;
  keel depth under the surface 0.276 m on B, 0.277 m on A.
- **For the steamer (#303)**: everything is a `BoatSpec`: columns (`PlaningHull(…, HullShape)` or
  any list: a 70 m hull is more columns, e.g. 14 × 4), mass, inertia (or the box default), centre
  of mass, drive (`BoatDrive`; paddles are a new value: thrust at two side points), `LiftShare` 0
  for a displacement hull, `WindArea`. The hull's pose is `BoatState.Attitude` about
  `Boat.Pivot`; a deck (`Rideable.Decks`, `DeckBuilder`) built in the visual's frame is carried by
  the posed visual, so its sections pitch and roll with the hull (the deck system reads the visual's
  transform every frame: `SectionFrame` is the posed `_visual`, `walk-aboard`). Trap for #303: a headless
  peer has no visual for a parked boat, so its deck frame there is the level body; give it an empty
  posed frame as `VehicleBody` does for a parked bus. Cost: one `WaterField.TryLevelAt` per column per substep.
- **Measured** (`--boatcheck`, calm unless said): jetski draft 0.23 m, on the plane in 1.4 s, top
  82 km/h (spec 81), bow up 7° over the hump, running trim 2-3°, circle 59 m at 64 km/h banked 11°;
  speedboat draft 0.28 m, plane 3.2 s, top 70.5 km/h (38 kn), hump 6.6°, trim 3°, circle 55 m at
  52 km/h. Gamey flat out: pitch ±10°, hops of 0.2-0.35 s off crests (the jetski sometimes throws
  its rider), the speedboat rolls 7° at worst. Parked gamey: heaves 1.3-1.7 m, keel within 5 cm of
  its draft under the surface.
- **Checks**: `tools/test.sh unit` (`BoatTests`: floats level, rights itself, hump and plane and top
  speed, turns without capsizing, a jet does not steer off throttle, gamey, parked drift, beaching);
  `--boatcheck jetski|speedboat[,shots] --chunks fixture:lake` (quick; `shots` windowed: pictures
  in `test_output/boats/`); `tools/boatnetcheck.sh` (net; `SHOTS=1`: B's view in
  `test_output/boatnet_B_*.png`).
- **Not done**: boats parked at real harbours (Nyon); a hull's collision box does not pitch (the
  hull boxes do, a parked boat's box stays level); wake foam lies where it was dropped, not on the
  moving waves; no water hiss/slap sound; boats in races have no water courses (the mount words
  `jetski`/`boat` parse); the jetski's rider is the motorbike rider (helmet).

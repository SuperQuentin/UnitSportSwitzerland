# Player, mounts and flight (`src/Player/`)

On foot, mounts, bike, skis, flight, feel layer, tricks, Game/Sim profile. Input bindings live in `src/Core/CLAUDE.md`; left-in-world vehicles in `src/Vehicles/CLAUDE.md`.

Index only: one line per note in `docs/notes/player/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/player`.

## Architecture

- `vehicle-hull-collision` — Cars and motorbikes collide as a box hull above the carrying capsule; no more cars sinking a third into each other; trucks and buses no wider than their body, getting out never inside the vehicle (#209, `--exitcheck`)
- `cockpit` — First-person driving (#69, trucks and buses #157): V cycles chase / cockpit with body / without, eye on the car body, head sway, held free look, seat and FOV settings, SubViewport mirrors (+0.5 ms), HUD setting, what is replicated, checks
- `third-first-person` — Third / first person: (`FootPlayer`, V / R3, saved as `GameSettings.ThirdPerson`, default third; `--view...
- `feel-layer` — Feel layer: (`Player/PlayerFeel`, child of the LOCAL `FootPlayer` only): sound, camera shake, speed lines,...
- `game-sim-profile` — Game / Sim profile: (`GameSettings.RideProfile`, Settings → Movement, `--profile game|sim`, default Game;...
- `tricks-landings-boost` — Tricks, landings, boost: (mounted, `FootPlayer`): hold Trick (F / RB) in the air and the stick flips (`_airPitch`)...
- `flying` — Flying: (`Player/Flight.cs`, meshes in `Avatar/AircraftMeshBuilder`): a `Flyer` is a `Rideable` whose `Step` is...
- `mantle` — Mantle: (on foot): pushing into a wall whose top is 0.45–2.1 m above the feet, with open air over it and standing...
- `steering-lean` — Steering by lean: (`Rideable.SteerByLean`): the input sets a target bank that eases in over ~0.2 s (out 1.6x faster)...
- `foot` — On foot: (`src/Player/FootPlayer.cs`): WASD + Shift at 1.6 / 4.6 m/s, Space to jump, plus two momentum moves — slide...
- `mounts` — Mounts: (`src/Player/Rideable.cs`): E opens a picker (`RideUi`) — On foot / Road bike / Skis. A vehicle is a table...
- `car-setups` — Car presets by category (#40): Everyday/SUV/Road racing/Rally-raid/All-terrain/Supercar/Rally over any car's spec, tyre grip per surface for cars, rough-ground term, raised bodies and off-road kit, replicated `CarSetupId`, `/race ... class=`, `--setupcheck`
- `motorbike` — Motorbikes: (`Motorbike`, `MotorbikeCatalog`, RideKind 64..95 append-only; #38, #41): R1, Monster and all 28 Honda Africa Twins (`docs/data/africa_twin_specs.json`), DCT, surface grip, wheelie/stoppie/friction-circle limits, `--motocheck`...
- `void-rescue` — Falling through the world (under terrain, into unstreamed void, under an interior floor) puts you back on the ground; safe spot per space; `--voidcheck`
- `crash-ragdoll` — Crash ragdoll (#214): through the windscreen as verlet joints, crash camera, bone-break and glass sounds, `PoseRagdoll` replication, floating in water (#380), `--wall`, `tools/crashnetcheck.sh`
- `player-overlap` — Two players set down on one spot ease apart (collision exception + 1.5 m/s nudge) instead of the solver throwing one kilometres (#203)
- `perf-visibility-on-change` (net) — `FootPlayer`'s `Sync`/`RelayNear`/`RelayFar`/`Vis` keep `VisibilityUpdateMode.None`; any new visibility input must call `RefreshNetVisibility`/`RefreshRelays` on change
- `perf-player-snapshot-size` (net) — `BodyPose`/`TrainPose` are not `[Export]`ed; `NetPose` carries them; only the landing squash survives as scale
- `perf-relay-delta-interval` (net) — `MakeRelay` keeps `DeltaInterval = 0.1f`; state read with `NetTime` stays `Always`, not `OnChange`

- `passengers` — Passengers (#158): seats from the models, `PassengerService` hands them out, riders moved and drawn from the host's copy, driverless vehicles when the driver jumps out, take the wheel (F / RB), hand-over between players, `--passengernet a|b|c`
- `walk-aboard` — Walking about in a moving vehicle (#162; ships' tilting decks, floor plan, reach by size #303): `VehicleDeck` from the model (ramps, flush door steps), decks as collision carried with the drawn vehicle, velocity measured from motion, hulls ignore their guests, steady/sway/full inertia (`--deck-inertia`, `/inertia`), seats by where you stand, E from outside on or off (#384, `--boardwalkable`), `--decknet a|b|solo`
- `trucks-buses` — Trucks and buses (#70): `HeavyCatalog` RideKind 96..119, trailers by code, a planar multi-body train (pins, per-axle tyres), sections as their own bodies, clutch/converter driveline in five shift modes, retarder, air, rollover, coupling, bus doors/kneel/destination, `--truckcheck`, `--truckprobe`, `--heavynet`
- `vehicles-sink` — Cars, motorbikes and trucks over their wading depth float, sink and are wrecked, the driver out swimming (#299); no blast under water; legacy 0.12 m lakes still drivable
- `swimming` — Swimming, diving, the air reserve (#301): `FootPlayer.Swim.cs` on `WaterField`, riding the waves, look-steered under water, mantle out, water landings by drag, drowning via `Health`, air bar, `PoseSwim` replicated, items holstered, `StartSwimming` for boats, wading and a crash ragdoll floating (#380), `--swimcheck`, `tools/swimnetcheck.sh`
- `boats` — Boats (#302): `BoatModel` hull columns on `WaterField` (buoyancy, slam, hump and plane, jet/rudder, beaching), `Boat` rideable (RideKind 121 jetski, 122 speedboat), `FootPlayer.Boat.cs`, `VehicleBody.Boat.cs` (float, drift, sleep, moored), remote copies on their own waves (`Heave`), parked collision posed as drawn on every peer (#378), thrown riders swim, foam on the waves and hull slaps (#380), steamer (#303) hooks, `--boatcheck`, `tools/boatnetcheck.sh`
- `landings` (world) — The steamer lies alongside Nyon's pier (#377), its gangway open onto the head; getting out of a boat beside a jetty steps onto it (`FootPlayer.Pier.cs`); the plank tilts to the pier (#383)
- `steamer` — The CGN paddle steamer (#303): RideKind 123, `BoatDrive.Paddle` (one shaft reversing through stop), telegraph/whistle/gangways, `SteamerMeshBuilder` decks (saloon, upper deck, stairs, plank), the hull's boarding ladders (#384, climbed like a gadget ladder), parked hull shaped as drawn (#378), the Nyon berth (deep water only), its sounds checked by their numbers (#380), `--steamercheck`, `tools/steamernetcheck.sh`

## Commands

- `commands` — Commands: --at, --path, --ride, --ridemenu, --shot

## Gotchas

- `perf-pose-mesh-cache` (avatar) — `ApplyFootPose` rebuilds the figure only on a new `FootPoseKey`, in place, remotes throttled by `HoldRemoteFigure`; a new input to the figure goes in the key (#221)
- `launch-clutch-bites-near-launch` — An automated clutch must bite near the launch speed, not from idle: biting at 600 rpm a diesel never got up to pull

- `player-scale-set-speed-size` — Player scale is set by speed, not by size: A 1.8 m capsule moving at 6-14 m/s reads as a giant next to 10 m...
- `isonwall-flickers-between-adjacent-physics` — `IsOnWall()` flickers between adjacent physics frames
- `held-movement-key-starts-state` — A held movement key that starts a state must be edge-triggered
- `feeding-collision-back-into-vehicle` — Feeding collision back into a vehicle needs `GetRealVelocity`, and a threshold
- `crouch-states-need-headroom-test` — Crouch states need a headroom test before standing
- `cars-drifting` — Cars and drifting: (`Player/Car.cs`, `CarSpec`, `RideKind` 8–10: Coupe 86, Rotary FD, Rally 4WD; issue #1). A car is...
- `what-others-see-what-owner` — What others see is what the owner sees: (`FootPlayer` pose sync, issue #9). A remote copy used to get position, yaw...
- `hitboxes-measured-never-typed` — Hitboxes are measured, never typed: (`Avatar/MeshBounds`, `Player/Hurtbox`). Movement keeps its capsule (a rigid 11...
- `replicated-value-whose-meaning-depends` — A replicated value whose MEANING depends on another replicated value must be reset when that one changes
- `simulate-network-s-rate-wall` — Simulate a network's rate in wall time, not frames
- `initial-d-roster-real-specs` — Initial D roster, real specs, racing: (`CarCatalog`, `RaceLine`, `DriveProbe`; #5). 26 cars, append-only, `RideKind`...
- `autopilot-raceroute` — AutoPilot and RaceRoute: the scripted racer for cars, lean-steered mounts and runners (`AutoPilot.For`), no speed cap, traffic sensing,...
- `racing-line-verge` — Racing line: the safe verge (`RaceLine.Widen`, #39): up to two wheels off the tarmac where height grid, trees, walls, water allow...
- `racecraft` — Racecraft (#52): velocity-aware traffic, oncoming anywhere on the road, hard blocked edges + `EdgeGuard`, passes, inside dives, no chop until a car length clear, road-wide sensing, jam resets; before/after numbers
- `slipstream` — Slipstream: `RideGround.Draft`, up to 45% less drag 2-25 m behind a vehicle within ±15°, cars and motorbikes
- `driver-skill-mistakes` — Driver skill/aggression per pilot (`Temperament`, NPCs from their id), braking points got wrong under pressure, spin/mistake logs, facing-back and jam resets
- `locked-rear-spins` — A mismanaged brake spins the car in Game too: arcade help fades while the foot brake locks the rear; `--spincheck`
- `pilot-brakes-below-rear-lockup` — A scripted driver brakes below rear saturation: 65/35 brakes and load transfer spun a car braking from 225 km/h...
- `animatable-body-is-static-body` — `AnimatableBody3D` is a `StaticBody3D`: filtering physics hits by `is StaticBody3D` also drops the traffic
- `tyre-wear-brake-wear-fade` — Tyre wear, brake wear and fade: (Settings -> Feel, off by default; `--tyrewear on`, `--brakewear on`; #20). Tyres...
- `car-soft-top-popups` — Car soft top and pop-up headlights: (`Car.Headlights`/`RoofOpen`, `CarRig`; #48). L lights (pop-ups rise), O roof on...
- `perf-no-per-frame-allocations` (general) — `PlayerFeel` HUD labels go through reused `StringBuilder`s + `SetText` (assign only on change); per-frame `InputHints` calls are memoised, `Tag()` concatenates
- `camera-arm-reach` — the on-foot and chase camera arms (pull-in, through doors) go through `ArmReach(..., margin, scale, min)`; never another copy (#221)
- `perf-camera-rays` — per-frame rays cast through a `Core.RayQuery` field with a cached exclude array (`SelfExclude`, `TrainRids()`, `WithShell`, `SeatExclude`); never `PhysicsRayQueryParameters3D.Create(..., new Array<Rid>{...})` per frame (#221)
- `perf-player-snapshot` — per-tick code reads `PlayerSnapshot.Of(GetTree())` (players' pos/vel/ride, built once per physics tick), never `GetNodesInGroup(FootPlayer.Group)` + LINQ; per-tick lists and ray queries are reused (#221)

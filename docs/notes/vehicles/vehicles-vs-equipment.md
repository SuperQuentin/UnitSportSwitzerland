# Vehicles vs equipment

- **Vehicles vs equipment** (`src/Vehicles/`). `Rideable.IsVehicle` (bike, helicopter, plane)
  splits machines that are **left in the world** from equipment that is worn and ends when taken
  off (skis, wingsuit, canopies). While driven, a vehicle still lives inside the driver's
  `FootPlayer` — the proven ride/flight physics, cameras and tricks are untouched. Getting out
  (E / Y, **anywhere, mid-air included**, with the vehicle's momentum), a crash, or being thrown off
  a bike hands its `VehicleState` to a `VehicleBody` (CharacterBody3D) that carries on alone: a bike
  rolls to a stop and tips over, a plane keeps its throttle and flies on until it hits something,
  a helicopter with no pilot **falls** (`FlightInput.Piloted = false` — autorotation needs a pilot).
  Getting in (E / Y within 3.5 m) hands the state back (`VehicleManager.Claim`). It sleeps at rest
  and only anchors collision streaming while moving. **Parked vehicles spawn 0.15 m up**: the
  terrain collision is a one-sided heightfield, and a box starting exactly on it fell 125 m through
  the mountain in five seconds. `FootPlayer.FindExit` stands the pedestrian on the *ground* beside
  the seat — measuring the uphill side at seat height read it as blocked and put the player on
  the vehicle's roof, which pushed the vehicle through the terrain.
  - **Engine** (`engine_toggle`, **I / D-pad ↑**, `FlightInput.Engine`): helicopter off → rotor spools
    down (0.18/s), lift fades below spool 0.6 into autorotation (9 m/s sink); on → ~3 s to lift.
    Plane off → zero thrust, it glides. Entering starts the engine.
  - **Damage**: vehicle HP (`FootPlayer.VehicleHealth`, `VehicleBody.Health`) loses `(impact−4)×10`
    per knock; past `CrashSpeed` or at 0 HP it becomes a **wreck**: `Explosion` (fireball, debris,
    smoke, flash, synthesised 3D boom) + charred visual burning 30 s, cleared after 90 s. Every peer
    watches the synced `Wrecked` flag and explodes it locally. Player: `FootPlayer.Health` 100, fall
    damage above 11 m/s landing, blasts via the static `Explosion.Blast` (each client hurts only its
    own player — client-authoritative), regen after 6 s, 0 → knocked out 3.5 s and revived at the
    last safe grounded spot. The occupant of a wreck is **thrown clear** and hurt by the blast.
  - **Network**: `World/Vehicles` + `World/VehicleSpawner` on server and clients (same path — RPCs
    route by it). Clients `RequestPark`; the server spawns for everyone with the parker as
    authority (it simulates, the server has no collision). `RequestClaim` is granted once — the
    server frees the node everywhere and returns its state — so two players cannot take one
    vehicle. A leaving peer's vehicles are removed. Untested with two real clients.
  - Check: `<godot> --path . -- --vehiclecheck[,out.png] --at 2585000,1110000` — 24 checks:
    helicopter up, bail out mid-air into wingsuit and canopy, empty helicopter falls and explodes;
    bike parked, stays, re-entered; plane engine off/on; plane crashed with the player in it.
    Location matters: at Riddes the engine-off plane glides into the mountainside.

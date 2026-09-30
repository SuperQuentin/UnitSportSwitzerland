# AutoPilot and RaceRoute

- **AutoPilot and RaceRoute** (`Player/AutoPilot.cs`, `Player/RaceRoute.cs`): the scripted racing driver
  and its road, shared by `--drivecheck` (all cars in one scene) and multiplayer races (`--raceauto`,
  see `src/World/CLAUDE.md`). `RaceRoute.BuildAsync` walks the main road from a point over the `.road`
  tiles (bridging RoadGen's trimmed junctions within 18 m), `FromPoints` rebuilds one a server sent.
  `AutoPilot.Drive(dt, go, others)` plugs into `FootPlayer.RideControls`; `others` are the other cars
  (position, speed, wreck?) for racecraft.
- **Every ground mount** (#39): `AutoPilot.For(route, player)` picks the pilot for what the player is
  on — a car (the drift/grip driver), anything **lean-steered** (road bike, skis, the motorbike: any
  `Rideable` whose `Step` banks through `SteerByLean`), or its own legs; null for a flyer. The lean
  pilot measures its mount instead of reading constants: a fresh `Rideable.Create(kind)` stepped on
  the flat for 2 s at full steer gives the max bank, 0.5 s of full brake against coasting gives the
  deceleration. Corners at `√(0.65·g·R·tan φmax)` (0.8 ran two R1s wide off a R 50 m bend: braking
  into it takes grip off the lean), rides round a stopped rider, pulls over right past the line, and
  is put back on the line after 3 s stopped off the road; brakes at 85% of the measured decel, steers by the
  bank a pure-pursuit arc needs (`tan φ = v²κ/g`, `steer = −φ/φmax`). On foot the runner walks through
  `FootPlayer.WalkControls` (a world-space wish, set by the pilot itself; `pilot.Go` holds it).
- **No speed cap**: the old `MaxSpeed = 42 m/s` (every car "top 151") is gone. The profile's straights
  are what the car's power, drag and rolling resistance allow, its braking is the car's published
  `BrakeDecel` (never more than the tyres), shared with the turn (friction circle), plus drag. Top
  speed then comes from the car model itself (gearing, rev limit, drag). Throttle gain rises from 0.35 at
  100 km/h to 1 at 170 km/h: a soft pedal only lags a profile that already is the car's power limit,
  and a firm one lower down power-overs an FD out of a bend. The steering
  look-ahead is 0.7 s up to 70 m (30 m at 250 km/h is a third of a second, and the hands saw), and
  aims wide of the line by the chord's sagitta `L²κ/8` (`AutoPilot.Aim`): pure pursuit cuts every
  bend by ~0.4 m at corner speed, which put wheels over the inside edge at apexes beside a drop.
- **Traffic**: see `racecraft` (#52) — velocities, oncoming anywhere on the road, hard blocked edges,
  passes, dives, no chop, slipstream, and the numbers. Besides the race's cars, `Sensed()` sweeps
  road-wide boxes along the route ahead and treats any non-static body as another vehicle — the local
  traffic (`AnimatableBody3D`), parked machines.
  Traffic yields only to the *local player*; `--drivecheck` sets `Traffic.Obstacles` to the race cars,
  or the traffic drove through the race and shunted cars back up the pass.
- Check top speed: `--drivecheck --at 2522700,1163026 --finish 1150 --cars N --traffic 0` (the longest
  straight in the Mollendruz data, ~1 km west of 2522.7/1163.0). The classification prints the car
  model's own flat-out speed over the same run: all six tested within 1 km/h of it.
- **Impacts in `--drivecheck`**: `FootPlayer` takes a knock off the speed at most 0.4 m/s a frame, so
  the old "one `Impacted` ≥ 2 m/s" rule never fired and crashes read "0 impacts" (the pre-#39 figures
  are not comparable). A knock is now the losses of one contact added up (events < 0.3 s apart), ≥ 2 m/s
  while touching something that is not the ground (trunk, car, traffic), or ≥ 5 m/s against anything —
  a hard launch alone reads as a 2 m/s knock because the body lags the model.
- Traffic makes races chaotic (35 cars around the camera on a 6 m pass): see `racecraft` for the
  before/after; the reference runs use `--traffic 0`.
- Targets run from the car's position projected on the line (`Along`), not the nearest point's arc:
  RoadGen's bridged junction gaps (up to 18 m between points) stalled runners aimed "4 m past the
  nearest point" for good.
  A car nose-to-tail with something stopped backs off and resets like one stuck off the road.
- `--drivecheck --mount K [--riders N]` races another mount (RideKind number, 0 on foot) with the pilot
  `For` picks; `--verge 0` skips the verge survey.

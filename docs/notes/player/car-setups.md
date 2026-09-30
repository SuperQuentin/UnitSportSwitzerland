# Car presets by category (#40)

- **What**: `Player/CarSetup.cs`. A `CarSetup` is a preset applied over a car's real `CarSpec`
  (`CarSetup.Apply`: same car, other parts); `CarSetups.All` is the list, **append-only**, the index is
  the wire id, 0 = Stock (the catalog car, `Apply` returns the spec itself). Every car takes every
  preset. Presets: Stock, Everyday ("lambda": all-season tyres, soft, open diff, economy map and
  gearing), SUV, Road racing, Rally-raid (off-road race), All-terrain (crawler), Supercar, Rally.
- **Physics** (all in the preset, all data): tyre (`TyreType`: tarmac factor on `CarSpec.Grip`, and
  absolute μ on gravel / grass / snow / ice; rock, forest, water derived), ride height (`Lift`, raises
  the CG by 70% of it), suspension `Travel` and `Stiffness`, `WheelScale` (rolling radius, so gearing),
  mass delta, power (torque curve and `PeakKw`), final drive, diff, drivetrain (RWD/FWD -> 4WD sets
  `RearBias` 0.6), CdA. `RefZeroTo100`/`RefTopKmh` are zeroed (no longer the published car).
- **Surface grip for cars**: `Car.Step` reads `RideGround.Surface` (`Surfaces.At`, looked up by
  `FootPlayer` now for cars too): `TyreType.Grip(surface, s.Grip)`. The stock `TyreType.Road` is the
  motorbikes' road-tyre table (gravel 0.65, grass 0.5, rock 0.8, snow 0.3, ice 0.1). **Not** for a
  race NPC in a stock car (its line is planned on tarmac grip and may use the verge); an NPC with a
  preset gets it.
- **Rough ground** (`CarSetups.Harshness`/`BumpGrip`/`RoughDrag`): harshness = roughness of the
  surface (gravel 0.35, grass 0.7, forest 0.9, rock 0.8, snow 0.4) × speed/20 × 0.15 m / travel ×
  √stiffness. It divides grip by `1 + 0.35 h` (wheels skipping) and adds `rough × (0.03 + 0.08 h²)`
  to the rolling resistance (soft ground, and a car bottoming out). Long travel is what makes an
  off-road car fast off-road; a lowered road racer on a meadow tops out near 45-65 km/h.
- **Look** (`CarBody.Lift/Tread/RoofRack/BullBar`, `Avatar/CarKit.cs`): `CarRig.Create` raises the
  body node by the lift (wheels stay on the road), `CarKit.Fit` adds tread blocks on the wheel spin
  nodes, a roof rack (spare, two olive jerrycans lying flat: upright and red they read as police
  lights) and a bumper bar with spot lamps. Bigger wheels force
  `Lift ≥ 2·ΔR`, or tyre tops go through the arches. All drawn mesh, so the hull (`FitHull`),
  hurtbox and parked box are measured from it; `Rideable.Measured` is keyed `(Kind, SetupId)` for cars.
- **Wire**: `FootPlayer.CarSetupId` (OnChange, beside `RideKindId`; reset by `ApplyRide` for a new
  car, rebuilt visual on change on every peer), `VehicleState.Setup` (`"setup"` in the dict, clamped
  by `CarSetups.Clamp` on the way in), parked `VehicleBody` rebuilds its car with it and `Capture`
  keeps it; `EnterVehicle` takes it back. NPCs: `PlayerReplication.NpcData` element 5, `RaceNpc.Setup`.
- **API for the garage (#56)**: `FootPlayer.SetCarSetup(id)` (at < 2 m/s; keeps lights and roof),
  `CarSetups.All/For/Parse/Slug/Ride(kind, id)`. The only UI is a data-driven `OptionButton` row in the
  travel picker (`RideUi.SetupRow`): applied to the car being driven and to a car picked from there.
- **Races**: `/race start [metres] [mount] class=<preset>` (name, slug or id): every car entrant is put
  in that preset on the grid (`RaceManager.Hold`), NPCs spawned for the race get it; `/race npc ...
  class=<preset>` gives NPCs a preset in a race of any class. The class travels in the `Setup` RPC.
- **With the garage (#56)**: the preset goes over the stock spec, the garage parts over that:
  `new Car(setup.Apply(stock), tuning)`, built by `CarSetups.Ride(kind, setup, tuningBits)`
  (`CarTuning.Ride` delegates to it). Grip: tarmac = `Spec.Grip × TyreModel.Grip`; off tarmac the
  preset's `TyreType` value, and a garage tyre with `Offroad` > 0 (Rally) claws back its share of the
  gap to 1, capped at the tarmac grip. `SetCarSetup` keeps the garage parts, `SetTuning` keeps the
  preset. `Car.ParkedBox` is the preset's look with stock garage parts and doors shut, keyed
  `(Kind, SetupId)`. `--garagecheck a <pw> --setup <preset>` runs the #56 check over a preset.
  The rough-ground term also lowers `--tuningcheck`'s gravel figures (Rally 0.76 g, Stock 0.53 g).
- **Checks**: `--setupcheck [car]` (headless, flat, Sim: wire round-trip, every preset on every car
  builds, table of 0-100/top on tarmac and 0-80/30 s on gravel and grass; fails unless the off-road
  presets beat the road racer off tarmac and the road presets beat SUV/crawler on it);
  `--avatars 3 out.png --carsetups [--car N] [--setups 0,3,2,4] [--view deg]` (one car in several
  presets side by side); `--ride car:N,S --setup <preset>` (prints 0-80 and the surface);
  `--drivecheck ... --setups 0,4,...` (per car in `--cars` order); `tools/switchcheck.sh` (loopback:
  the watcher must see the Rally-raid preset on the driven and on the parked car; the driver logs
  in as admin, or the server refuses to park the car it conjured).

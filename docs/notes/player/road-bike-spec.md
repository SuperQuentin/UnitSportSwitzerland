# Road bike spec: a real bike, its chart and the rider's fit (#1124)

- **`BicycleCatalog`** (`src/Player/BicycleCatalog.cs`): a `BicycleSpec` per real bike, model, size and
  build. It holds the maker's geometry chart (`BicycleGeometry`: stack, reach, head and seat angles,
  head tube, chainstay, BB drop, fork offset, crank, stem, spacers, bar reach, drop and width), the bike's
  mass, the tyre width, chainrings, cassette, groupset, brakes and a `BikePalette` look. Entry 0
  (`Default`) is the road bike everyone rides: the owner's **Scott Addict RC 15 (2018), S/52, black, Ultegra
  Di2 disc, 52/36 × 11-28, 8.0 kg**. The comment on the entry says which numbers are the chart's and which
  are assumed until measured on the real bike (BB drop, tyres, cranks, cockpit).
- **`BikeFrame`** derives every point from a spec: the axles, head tube, steering axis, seat cluster,
  stem and bar, hoods, drops and pedals, and the trail. The chart's wheelbase is *not* an input. The
  derived one must land within 1.5 cm of it (`BicycleFrameTests`), which catches a mistyped number.
- **The fit**: the saddle goes up the seat tube's line until the figure's leg (thigh 0.435, shin 0.415)
  is bent 35° at the knee with the pedal at the bottom. The hands are on the hoods.
  `HumanMeshBuilder.CyclingRig` solves the upper body from the hip and the wrist (the seated rider's
  shoulder solve, `RiderRig`). `Cyclist`'s pedalling legs use the same `BikeFrame.LegHip` / `Pedal`, so
  the still pose, the moving legs and the cranks cannot disagree.
- **Mass**: Sim is `GameSettings.RiderMass` (Settings → Gameplay → Rider weight, `--rider-kg`, default
  72.5) + the spec's mass + 1.5 kg of bottles. Game keeps the 72.5 kg reference rider, so its feel does
  not change with the setting. The default totals the old 82 kg, so `--ride bike`'s references hold.
- Every rider's bike is the catalogue's look (`BikePalette.ForRider`). Riders are told apart by their kit.
  Choosing a bike per player, and replicating that choice, is for when a second bike exists.
- **Longitudinal physics** (`BikePhysics`, pure, `BikePhysicsTests`; phase 2):
  - The equation is `(m + I/r²)·a = η·P/v − ½ρ·CdA·(1 − draft)·v² − Crr·m·g·cosθ − m·g·sinθ`.
  - Wheel inertia: `BicycleSpec.WheelInertia`, 0.15 kg·m², which adds 1.3 kg.
  - η = 0.975, applied to every watt. A hub-measuring trainer already counts the chain, so it is
    charged twice until RideLink says which device the watts come from.
  - ρ from the standard atmosphere at the rider's LV95 altitude, 15 °C at sea level (no weather yet).
  - Crr by `RideGround.Surface` (`Bicycle.RollingResistance`): the bike now asks `Surfaces.At` like
    the motorbike does.
  - CdA = 0.32 × (rider kg / 72.5)^0.425 × position.
  - **Position is automatic, no key**: hoods, drops (×0.88) while sprinting, tuck (×0.75) freewheeling
    downhill above 40 km/h, eased over 0.5 s. The figure does not change pose yet.
  - **Wheel draft** (`RideGround.WheelDraftBehind`): from 4 m/s, by wheel-to-wheel gap and lateral
    offset — 40% at 0.5 m, 35% at 1 m, 21% at 3 m, 0 past 15 m or 0.6 m aside. Cars keep their
    2-25 m tow.
  - Reference (500 m, tarmac, 180 W): 72.5 kg rider 32.9 km/h, the owner (120 kg) 29.6, 31.1 on the
    Grimsel, 24.4 on gravel, 33.6 a metre behind a wheel. In game, `--ride bike,60 --world flat
    --profile sim [--rider-kg 120]` (sea level) reads 32.1 / 28.3 km/h at 60 s, still settling.
- Next phases (drivetrain, braking, steering dynamics): `docs/plans/road-bike-realism.md`.

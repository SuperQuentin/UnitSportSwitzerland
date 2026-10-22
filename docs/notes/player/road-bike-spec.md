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
- Next phases (drivetrain, braking, steering dynamics): `docs/plans/road-bike-realism.md`.

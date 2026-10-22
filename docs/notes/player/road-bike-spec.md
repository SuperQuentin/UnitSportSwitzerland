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
- **Gears** (`BikeDrivetrain`, pure, `BikeDrivetrainTests`; phase 3): the spec's rings and cassette.
  `Bicycle.Gears` shifts itself, as Di2's synchro shift does.
  - The rider's thumb is a cadence window: harder above 97 rpm, easier below 80, a cog every 0.25 s.
  - It shifts only while pedalling: the chain cannot cross cogs while freewheeling.
  - The ring changes on the inner ring from the 15 cog harder, and on the outer ring into the 23 cog
    easier, with the rear moving two cogs back. 13 steps cover 36×28 to 52×11.
  - Cadence = speed / development. The legs turn at it, freewheeling stops them, capped at 150 rpm.
  - Key and pad watts × `PowerAtCadence`: 1 at 90 rpm, 0.86 at 60 and below, 0.75 at 130. So a
    120 kg rider in 36×28 up 8% grinds at ~50 rpm and loses 14%.
  - `Bicycle.PowerMeasured` (a trainer or power meter: no curve) and `MeasuredCadence` (the sensor
    drives the legs) are the seams for #969 and #903.
- HUD: `29 km/h    52×19    88 rpm`. `--ride bike,N` prints the gear, cadence and position every second.
- **Braking and grip, Sim only** (`BikePhysics.Brake`, `GripLean`, `BikeFrame.Balance`,
  `BikeBrakingTests`; phase 4). Game keeps its arcade 7.5 m/s² brake and 0.88 rad lean.
  - The lever goes 70% front. Braking moves the load forward by `a·h/L`, so the rear grip shrinks
    until it locks and slides at 0.8 of peak (`Bicycle.RearLocked`, the feel layer's dust).
  - The front is capped by its grip and at 0.95 of the pitch-over point `g·x_f/h`.
  - The centre of mass: rider 20 cm over the hips and 12 cm ahead; bike over the bottom bracket.
    The owner is 37% front at 1.06 m, so the limit is ~0.57 g.
  - A full pull stops from 50 km/h in ~17 m. On gravel (μ 0.65) it is still pitch-limited, with the
    rear locking sooner. On grass (0.5) the tyres give out first.
  - Discs pull 1.1 g at the lever, rims 0.9.
  - Lean = `min(43°, atan √(μ² − (a/g)²))` (μ = `Motorbike.SurfaceGrip`, tarmac 0.9): 42° dry,
    ~37° braking at half a g, 33° on gravel.
  - No wet roads: the game has no rain yet.
  - In game: `--ride bike,45 --brake-at 40 --profile sim --rider-kg 120` drops 26.6 → 6.5 km/h in
    the first second, `REAR LOCKED` in the trace.
- **Steering: the Whipple bicycle, Sim only** (`BikeDynamics.cs`, `BikeDynamicsTests`; phase 5).
  Game keeps `SteerByLean`.
  - `WhippleModel` holds the Meijaard et al. 2007 matrices (M, C1, K0, K2). The unit test holds
    them to the paper's published benchmark values.
  - `WhippleParameters.For(frame, riderMass)` fills them from the Addict: head 72°, trail 64 mm,
    wheelbase 0.98 m, the wheels' inertia, and the rider's mass and centre of mass (the body's
    inertia is the benchmark's scaled by mass).
  - `RiderBalance`: the input asks for a lean (+ right, the paper's signs). The rider sets the steer
    that holds the present lean, plus a spring toward the wanted one (6 rad/s, ζ 0.85: 63% in
    0.3 s). The arms are an 18 rad/s servo; the roll row is integrated at 1/360 s.
  - **Countersteer comes out of the equations**: to lean right the bars go left first (tested at 5
    and 12 m/s). The steer a lean needs falls as 1/v², which is why a fast bike is steady.
  - Below 1.5 m/s the bars steer directly and the bike is held upright (walking or wheeling it).
  - Large leans: the linear model turns as φ, not tan φ, so the yaw is scaled by tan φ/φ.
  - The bars are drawn turning: `BikeMeshBuilder.Parts.Front` about the steering axis,
    `Cyclist.SteerAngle`. Replicated in `WritePose`'s Z.
- **The autopilot on it** (`Rideable.SteerLag`, `AutoPilot.LagLook`): a countersteered turn starts
  with a moment's yaw the wrong way, so chasing a point 0.8 s ahead weaves down a straight. Predicting
  the heading by the yaw rate made it worse: it feeds that wrong-way yaw back. Looking 2.5 × the lag
  further ahead fixed it.
  - `--drivecheck --mount 1 --traffic 0 --seconds 240 --chunks fixture:hairpin --profile sim`:
    131.8 s (old steering 134.1), no impacts, mean 0.48 m off the line, 58 m off tarmac (old 31).
- **The rider's body, Sim only** (`RiderPhysiology`, `RiderPhysiologyTests`; phase 6).
  - Critical power: `GameSettings.RiderCriticalPower`, Settings → Gameplay → Threshold power,
    `--rider-cp`, default 240 W. The reserve W′ is 20 kJ.
  - Above CP the watts over drain W′. Below it, Skiba's differential model refills it,
    `dW′/dt = (W′ − W′bal)·(CP − P)/W′`.
  - Keys and pad watts are held to `Available`: all of it while the reserve lasts, fading to CP over
    its last 8%. A 520 W sprint on a 240 W threshold lasts ~71 s.
  - Measured watts (`PowerMeasured`) only drain the reserve shown.
  - Heart rate eases to `TargetHeartRate`: rest + (max − rest) × (0.15 + 0.75·P/CP + 0.1 × the
    reserve spent). 60/185 by default, rising with τ 25 s, falling with τ 45 s. A strap
    (`MeasuredHeartRate`) overrides it.
  - HUD: `… rpm    148 bpm`, and the Game boost bar shows W′ in Sim. `--ride bike,N` prints both.
- **Standing** (`RidingPosition.Standing`, phase 6): pedalling up more than 8% under 70 rpm. CdA
  ×1.15, and the keys' cadence curve floors at 0.95, since the rider's weight turns a cadence seated
  legs cannot.
- **Poses per position** (`BikeFrame.Fit`, `HumanMeshBuilder.BuildCyclist`, `Cyclist.RidingPose`):
  each solved from the bike's points, as the hoods pose is.
  - Drops: hands on the drops, 12.5 cm lower.
  - Tuck: hips on the saddle's nose, hands on the tops by the stem. The back is set at 12°
    (`RiderRig`'s `backAngle`) and the elbows fold to the bars: shortening the reach instead pushed
    the shoulder *up*.
  - Standing: hips 7 cm up and 15 cm forward, arms long. The bike rocks ±7° once a crank turn, away
    from the pushing pedal, and the rider counter-rocks 70% of it.
  - A pose shows after 0.3 s held, rebuilt only then. The legs' crank-step cache is keyed by
    position too. `FirstPersonEye` follows the pose.
  - Replicated in `WritePose`'s W.
  - The model viewer's "Road bike riding position" shows all four.

# Motorbikes

- **Motorbikes** (`Player/Motorbike.cs`, `Player/MotorbikeCatalog.cs`, mesh `Avatar/MotorbikeMeshBuilder.cs`;
  #38). Data-driven like the cars: a `MotorbikeSpec` per bike in `MotorbikeCatalog.All`, whose index
  sets the `RideKind` (**64..95, append-only**, replicated as an int; the next other mount is 96).
  Adding a bike is adding an entry: engine layout (sound), torque curve, gears + primary + final,
  `Dct` / `QuickShifter` (drive cut per up-shift: 0.02 / 0.07 / 0.25 s), wet mass, CG height and
  rear weight share, grip, max lean, CdA, braking, reference 0-100 and top speed, and a `MotoLook`
  (style Sport / Naked / Adventure, engine shape, wire spokes, tyre sizes as on the sidewall —
  `"90/90-21"` works — wheelbase, rake, trail, travel, seat / grip / peg points, colours).
  Rider mass 75 kg is added to the wet mass.
- **Model**: the bicycle's `SteerByLean` plus the car's engine and gearbox (automatic sequential,
  up at 97% of the redline, down below half the peak-power rpm; a slipping clutch holds `LaunchRpm`
  at a launch). Two-wheeler limits: drive is capped at **90% of the wheelie point** `g·b/h` (b CG to
  rear axle) and at rear traction `μ·g·a_f/(L − μ·h)`; braking at 90% of the **stoppie point**
  `g·a_f/h` and the tyres; the lean the tyres give is what the friction circle leaves after
  accelerating or braking, `tan φ ≤ √(μ² − (a/g)²)` (braking hard stands it up). Top speed is not a
  number anywhere: it is where power meets `½ρ·CdA·v²·v`. Game adds 0.1 rad of lean, a quicker
  roll-in, 15% grip and 10% brakes; the engine stays the real one.
- **Specs**: Yamaha YZF-R1 (2020+): 200 PS / 13,500, 112.4 N·m / 11,500, 6 gears 2.600-1.250,
  primary 1.512, final 2.563, 201 kg wet, 1,405 mm, 120/70ZR17 + 190/55ZR17, 299 km/h limited.
  Ducati Monster (2021+, 937): 111 hp / 9,250, 93 N·m / 6,500, 188 kg, 1,474 mm, 120/70ZR17 +
  180/55ZR17. **Assumed** (commented at each entry): torque between the peaks, redlines, CG heights,
  weight split with the rider, CdA (R1 0.34 tucked, Monster 0.44 upright), the Monster's gearbox
  (821/939 ratios, primary 1.85, final 43/15), braking, refs from magazine tests.
- **Measured** (`--motocheck`, flat, no world; `--ride` on the Jura tiles): R1 0-100 3.27 s (ref ~3.1),
  0-200 6.9 s, top 305 km/h flat out (ref 299 limited; 301 on terrain at 20 s), 100-0 36.7 m;
  Monster 0-100 3.12 s (ref ~3.4), top 232 km/h (ref ~230), 100-0 36.4 m; both hold 90% of the
  wheelie / stoppie limit, never past it.
- **Remote players**: the lean is in the replicated `BodyPose`; `WritePose` sends bar angle, rear
  wheel spin RATE, rpm01, brake, and `AnimateRemote` integrates the spin. The engine is heard by the
  rider (`PlayerFeel`, through `IEngined`, shared with cars) and from a parked bike (`VehicleBody`),
  not yet from another player's moving bike (cars are not either).
- **Riding position** is solved from the contact points (`HumanMeshBuilder.AppendRider`): pelvis on
  the seat, hands on the grips with the arm at 80% reach, so the shoulder is the upper intersection of
  the torso and arm circles — low clip-ons fold the R1 rider onto the tank, the Monster's high bar
  sits its rider up. Full-face helmet.
- **Check**: `<godot> --headless --path . -- --motocheck` (non-zero on a miss);
  `<godot> --path . -- --ride r1|monster|moto:N,20[,shot.png] [--heading deg] [--profile game]`
  (prints 0-100 / 0-200 and the worst wheelie/stoppie use); `--avatars 3 out.png --focus r1|monster`
  (5 + N); `--synccheck` has a `moto` stage (bar angle + lean on the mirror).
- **Found on the way**: (1) `MeshScratch.Box` was wound inside out (counter-clockwise from outside),
  so every box showed its far inner walls — invisible on a plain block, but a head showed straight
  through a helmet; fixed at the source, all boxes now render their near faces. (2) The collision
  feedback smoothed the achieved speed, which lags any launch by `a / ImpactResponse`: every
  acceleration above 6 m/s² read as a wall and was clamped (the R1 did 0-100 in 5.2 s). It now
  smooths the shortfall (commanded − achieved) instead; see `feeding-collision-back-into-vehicle`.

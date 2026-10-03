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
- **Check**: `<godot> --headless --path . -- --motocheck [N,M,...]` (non-zero on a miss; all bikes, or catalog entries N, M);
  `<godot> --path . -- --ride r1|monster|moto:N,20[,shot.png] [--heading deg] [--profile game]`
  (prints 0-100 / 0-200 and the worst wheelie/stoppie use); `--avatars 3 out.png --focus r1|monster`
  (5 + N); `--synccheck` has a `moto` stage (bar angle + lean on the mirror).
- **Found on the way**: (1) `MeshScratch.Box` was wound inside out (counter-clockwise from outside),
  so every box showed its far inner walls — invisible on a plain block, but a head showed straight
  through a helmet; fixed at the source, all boxes now render their near faces. (2) The collision
  feedback smoothed the achieved speed, which lags any launch by `a / ImpactResponse`: every
  acceleration above 6 m/s² read as a wall and was clamped (the R1 did 0-100 in 5.2 s). It now
  smooths the shortfall (commanded − achieved) instead; see `feeding-collision-back-into-vehicle`.

## Honda Africa Twin, every variant (#41)

- **28 entries, RideKind 66..93** (catalog index 2..29, chronological): XRV650 RD03 (EU, Japan),
  XRV750 RD04 (+ the German 50 PS), RD07, RD07A; CRF1000L 2016-17 (std, ABS, DCT), 2018-19 (MT,
  DCT), CRF1000L2 Adventure Sports (MT, DCT, Japan Type LD); CRF1100L 2020-21, Adventure Sports
  2020-23 (MT, DCT, ES, ES DCT), 2022-23, 2024-26 (MT, DCT, ES, ES DCT), Adventure Sports ES 2024-26
  (MT, DCT). Room is left for two more bikes (94, 95).
- **Sources**: `docs/data/africa_twin_specs.json`, one object per variant with a source URL per
  field (Honda Japan release tables for every gear ratio, primary and final reduction, rake and
  trail; hondanews.eu press kits; MOTORRAD; inSella). Each catalog entry names its object by `id`
  and repeats its main URLs. The entries are generated from the file, so every published number
  (power / torque peaks, ratios, kerb mass, wheelbase, rake, trail, seat, clearance, travel, tank,
  tyres, disc count and size, official colour names in `MotorbikeSpec.Colours`) is the dataset's.
- **Assumed** (no source; commented per family at the top of the Africa Twins and per entry):
  redline 8,200 (CRF, XRV750; XRV650 8,500), idle, launch rpm, the torque curve between the peaks,
  CG 0.74-0.76 m with the rider and 50% rear, tarmac grip 0.95 / 1.0, 42° / 45° lean, CdA (0.50
  XRV750, 0.52 XRV650, 0.57 CRF, 0.59 Adventure Sports), braking 7.5 / 8.5 / 9.5 m/s², the XRV's
  52° V firing 232-488 (Honda only says "offset dual-pin"; the 76° offset usually quoted is
  180 − 2·52, which gives a 90° twin's primary balance and fires 128° of crank apart), XRV front
  travel (230 / 220 mm from Wikipedia, unverified), the Type LD's travel. Where sources disagree
  the dataset's EU value is used (CRF1000L trail 115 vs 113, 2024 AS wheelbase 1,550 vs 1,570).
- **Measured** (`--motocheck`, Sim, flat, 75 kg rider) against the dataset's three measured tests:
  XRV750 RD07A 0-100 4.83 s / 180 km/h (MOTORRAD 14/1999: 4.6 s / 180); CRF1000L 2016 3.70 s /
  200 km/h (MOTORRAD 03/2016: 3.8 s / 199); CRF1100L Adventure Sports ES DCT 2024 3.42 s /
  204 km/h (inSella: 3.44 s / 202.8). The unmeasured bikes follow from their data: CRF1100L 206,
  CRF1000L 200, XRV750 RD04 178, 50 PS 163, XRV650 168-173 km/h; DCTs ~0.25 s quicker to 100
  (no drive cut per shift). 100-0: 39-40 m CRF, 42.6 m XRV750, 47.7 m on the XRV650's single disc.
- **DCT** (`Motorbike.ShiftDct`, D mode): the up-shift rpm rides on the throttle (42% of the
  redline when cruising, 97% flat out), down-shift when the rpm sags (27-55% of peak-power rpm) or
  on a kick-down; neither shift is taken if it would land where the other would fire, and a shift
  holds 0.6 s. `--motocheck` fails a DCT that down-shifts flat out or reverses a shift at a steady
  35% throttle (0 reversals on all 13 DCTs).
- **Surface grip** (all motorbikes): `RideGround.Surface` is `Surfaces.At` under the wheels (the
  `.road` surface, else the cover), looked up by `FootPlayer` for motorbikes and cars (cars: `car-setups`).
  `Motorbike.SurfaceGrip`: tarmac keeps the tyre's own grip; off it the ground sets the limit — a
  road tyre gets 0.65 on gravel / a dirt road, 0.5 on grass (rock 0.8, snow 0.3, ice 0.1) whatever
  its compound — and `MotorbikeSpec.OffroadTyre` claws back that share of the gap to 1 (Africa Twin
  CRF 0.3, XRV 0.4; R1 and Monster 0). It feeds traction, braking and the lean the friction circle
  allows. Grass 0-100: R1 9.9 s, 2024 AS DCT 6.6 s; the lean at 80 km/h drops from 45° to 38° on
  gravel and 34° on grass. No suspension term: nothing in the model makes rough ground punishing
  yet, so long travel is data (mesh) only.
- **Rides** (`--ride moto:29,7 --at ... --heading ...`, 2024 AS ES DCT): a straight dirt track at
  2518512,1165412 heading 301 — on Gravel throughout, 0-100 5.43 s, 59% of the wheelie limit
  (traction-limited); across meadow at 2518038,1167321 heading 0 — on Grass, 0-100 6.70 s, 47%.
  The R1 on the same grass reaches 70 km/h in 7 s. The probe prints the surface and gear.
- **Sound**: `EngineLayout.ParallelTwin270` (the 270° crank fires 270-450 like a 90° V-twin: the
  `VTwin90` voice with a longer pipe) and `EngineLayout.VTwin52` (232-488). `--soundcheck` renders both.
- **Look** (`MotoStyle.Adventure`, `MotorbikeMeshBuilder.Adventure`): tank sized by `TankLitres`
  between radiator shrouds, long flat seat onto a luggage rack, high silencer on the right, sump
  guard at `GroundClearance`, knuckle guards, spoked 21/18 (19 on the 2024 Adventure Sports),
  `FrontDiscs` / `FrontDiscMm`; CRF: narrow cowl, two LED lamps side by side under a DRL line, the
  beak under them, screen of `ScreenHeight`; XRV (`HalfFairing`): a broad frame-mounted fairing
  with two round headlights; Adventure Sports: `CrashBars`, taller screen. Liveries use `Paint`,
  `Trim`, `Accent`, `Frame`, `Wheel`, `SeatColor` (tricolour = white, blue, red, gold rims).
  Checked with `--avatars 3 out.png --focus 5+N [--view 145]`: XRV750 RD07A 12, CRF1000L 13,
  CRF1100L AS ES DCT 2024 34.
- **Picker**: the motorbikes left `Rideable.All` and fold open on their own page like the cars
  (`RideUi.Fold`; number keys: the mounts, then Cars, then Motorbikes).
- **Not verified**: a real two-process server + client session (`--synccheck` covers the
  replication path with an `africa` stage on the last Africa Twin); the XRV's exhaust layout and rim
  colours (the dataset marks them unverified; one silencer on the right is drawn).

## More brands, a second RideKind range, folders, wheelies (#410)

- **RideKind**: 64..95 filled up with the Africa Twins; entries 32 onwards take **128..191**
  (`MotorbikeCatalog.First2`; the next other mount is 192). `For` / `IsMotorbike` map both ranges;
  probes take `All[n].Kind`, never `First + n`.
- **Generated entries**: `tools/motorbikes/gen_catalog.py` reads `docs/data/yamaha_fazer_r3_tmax_specs.json`,
  `yamaha_tenere_tracer_kawasaki_versys_specs.json`, `ktm_honda_cb500_specs.json` (same keys as the Africa
  Twin file plus `brand`, `family`, `drive`, `cvt`, `livery_main_colour`) and writes
  `src/Player/MotorbikeCatalog.Brands.cs` (`Imported()`, concatenated after the hand-written 30).
  Append-only: new files at the end of `DATASETS`, new variants at the end of their file. Each entry's
  comment lists its sources and an "Assumed:" line. What the script assumes: per style (Sport, Naked,
  SportTouring, Adventure, Supermoto, Scooter) the CG, rear share, CdA, grip, lean, brakes and riding
  position; redline (peak-power rpm + 500..1,500), idle, launch, the torque curve between the published
  peaks; a missing box borrowed from a sibling (`GEARS_FROM`) with the overall top gear **fitted** to the
  claimed (else drag-limited) top speed at 105% of peak-power rpm; single gaps in `BORROW` / `FILL`; the
  livery from the dataset's colour words. The Tracer 900 / 9 CdA is 0.59, fitted to the Tracer 9 GT's
  measured 214 km/h. Every `livery_main_colour` is unverified.
- **CVT** (`MotorbikeSpec.Cvt`, TMAX): the ratio runs from `Gears[0]` to `CvtHigh`, holding the engine at
  `Lerp(LaunchRpm, CvtRpm, throttle)`; `Primary` / `FinalDrive` are the reductions around the belt
  (TMAX 500: 2.659 and 2.262; 530/560: 1.0 and 6.034 / 5.771). No shifts.
- **Sounds**: `EngineLayout.Single` (LC4), `ParallelTwin180` (CB500, R3, Versys), `ParallelTwin360` (TMAX),
  `Triple` (CP3), `VTwin75` (LC8 and the LC8c parallel twin, 285-435), `Inline4Bike` (Fazer).
- **Wheelies** (`Motorbike.Step`): hold {tuck_boost} (`RideInput.Effort`) to pull back: the wheelie
  control is off (drive up to traction, capped at μ·g) and the rider's CG moves 5 cm back. A press on
  >50% throttle pops the clutch, `PitchRate += 2·clamp(drive / (m·g·b/h), 0.6, 1.2)` rad/s. Then the bike
  pivots on its rear contact: `θ'' = (a·h' − g·b') / (r² + 0.32²)` with the CG at `α₀ − θ` from vertical;
  front up, only the rear brake works (4.5 m/s²). Let go and the drive is cut under `0.6·g·b'/h'` until the
  front lands. **Sim**: past the balance point + 0.3 rad it loops out (`LoopedOut`) and
  `FootPlayer.ThrowFromVehicle(loopOut: true)` drops the rider off the back ("LOOPED OUT!"; the bike is
  parked upright where it was). **Game**: the throttle picks an angle (25-85% of the balance point) and a
  PD on the pitch holds it with the full drive of the gear (shift cuts ignored), never past balance.
  The pitch rides in the pose's w (`Pitch + 10` while braking) and `Motorcyclist.Pitch` turns a pivot at
  the rear contact (a pillion does not pitch).
- **Measured** (`--motocheck`, wheelie line): a clutch pop at 25 km/h lifts every bike 12-20° in Sim;
  Game holds a 60% wheelie for 5 s on every bike; Sim flat out with the pull held loops every bike out
  within 3 s. Generated bikes: FZS1000 0-100 2.98 s / 249 km/h, R3 5.57 s / 176, TMAX 560 5.35 s / 162,
  XT1200Z 3.53 s (MOTORRAD 3.7) / 214, Tracer 9 GT 2.92 s (inSella 3.1) / 215 (214), Versys 650 4.3-4.6 s.

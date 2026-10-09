# Initial D roster, real specs, racing

- **Initial D roster, real specs, racing** (`CarCatalog`, `RaceLine`, `DriveProbe`; #5). 28 cars (34 XP90 Yaris, 35 XW20 Prius: #464), append-only,
  `RideKind` = 8 + index (8..63 reserved for cars). Each `CarSpec` carries the real car: crank torque curve
  (`Torque`, interpolated by `TorqueAt`), OEM `Tyre` (rolling radius from the sidewall), published braking
  (`BrakeDecel`), `Differential` (an open diff stops pushing at 72% of the driven axle's grip, so it will not
  power over like an LSD car), `Style` Drift/Grip as in the series, and published `RefZeroTo100`/`RefTopKmh`
  that `--driftcheck` compares the model against (all within ±15% / ±8%; figures are from recall, not
  source-checked). Game: every car must hold a drift (the throttle keeps the rear sliding once sideways,
  more for FF); Sim: grip cars need not slide.
  **`--drivecheck --cars 0,1,3,4,12,6`** races them all at once, colliding, down the main road from the
  spawn: a minimum-curvature `RaceLine` inside the tarmac (curvature over ±8 m AND ±4 m — RoadGen's
  junction gaps leave 15-20° kinks the wide window hid), a per-car quasi-steady speed profile (corner
  `√(μg/κ)`, crest `√(gR)`, forward power/traction pass, backward braking pass), drift planning only for
  Drift cars by stepping `Car.Clone()` through a handbrake entry (committed only if the sim stays 1.5 m
  inside the edge), soft-hands catching of unplanned slides, reverse-out when stuck, passes on straights.
  `--record prefix` writes a GPX per car with the nose yaw (`<us:yaw>`); the GPX replay plays it as a car
  (`<type>car:N</type>`, `Runner.KeepOutside` keeps every cinema lens out of the body) — that is how a race
  is shown in Absolute Cinema. Traps found: the brake at a standstill selects REVERSE (the grid held the
  brake and reversed off the line — hold the handbrake); a reversing car has 180° of slip and is not a slide.
- **Adding a car, two traps (#464)**: the automatic gearbox shifts up at 0.94 × `Redline` and down
  under 0.55 × `PeakRpm`; if first-to-second drops the revs under that, the car hunts 1-2 forever and
  tops out in first (the Yaris at 6,500 rpm did 43 km/h; check `Redline·0.94·g2/g1 > PeakRpm·0.55`).
  And `--driftcheck` Game wants every car to hold a drift: a 0.9 eco-tyre `Grip` did not (14° max).
- **Gearbox and drive side (#760)**: `CarSpec.Gearbox` is `Stepped` (a lever and a clutch, shifted
  by the automatic logic) or `ECvt` (the XW20 Prius). An e-CVT's `Gears` are the two ends of its
  range; `Car.Step` holds the engine at `_cvtRpm` (just off idle at a light foot, `PeakRpm` floored,
  swept at 4,000 rpm/s) and the ratio follows the road speed, clamped to the range; no shifts. Its
  engine stops on the motor alone (`EngineAsleep`: reverse, off the pedal, or under 0.25 pedal and
  13 m/s, waking over 0.35 or 15 m/s): `Rpm01` 0, silent in `PlayerFeel`, the tach at zero. The top
  ratio sets the top speed (the redline caps it): 0.84 gave 184 km/h, +8% on the 170 published.
  The Yaris and Prius are left-hand drive (`CarBody.LeftHandDrive`), the Japanese cars right-hand.

# Initial D roster, real specs, racing

- **Initial D roster, real specs, racing** (`CarCatalog`, `RaceLine`, `DriveProbe`; #5). 26 cars, append-only,
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

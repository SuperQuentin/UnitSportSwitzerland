# An automated clutch must bite near the launch speed, not from idle

- The heavy driveline's automated clutch first closed in proportion to how far the engine was
  above idle. At half throttle a diesel makes little torque at 600 rpm, and a clutch already
  biting there took all of it: engine and clutch settled at ~590 rpm, 0.14 closed, and a loaded
  semi on full lock never moved (seen in `--heavynet`, not in the offline checks, which launch
  flat out). It now bites only from 200 rpm below the launch speed the throttle asks for
  (`HeavyDriveline.AutoClutchTarget`), as a real AMT's launch control holds the engine up.
- The same trap one level up: the launch speed has to sit above the automatic's down-shift point,
  or the box downshifts the moment the clutch locks and a loaded truck on 12% hunts down to first.
- That alone was not enough loaded on a slope: at half pedal the engine still made too little at
  800 rpm to climb to the launch speed. A real AMT asks the engine for **speed** during a launch,
  so while the automated clutch slips the engine is fuelled toward the launch rpm (up to full
  torque) whatever the pedal says (`HeavyDriveline.EngineStep`).
- `--truckcheck` pulls away at half throttle on full lock with 39 t, on the flat and up 5%.

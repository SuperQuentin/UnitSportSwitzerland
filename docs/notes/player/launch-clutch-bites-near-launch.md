# An automated clutch must bite near the launch speed, not from idle

- The heavy driveline's automated clutch first closed in proportion to how far the engine was
  above idle. At half throttle a diesel makes little torque at 600 rpm, and a clutch already
  biting there took all of it: engine and clutch settled at ~590 rpm, 0.14 closed, and a loaded
  semi on full lock never moved (seen in `--heavynet`, not in the offline checks, which launch
  flat out). It now bites only from 200 rpm below the launch speed the throttle asks for
  (`HeavyDriveline.AutoClutchTarget`), as a real AMT's launch control holds the engine up.
- The same trap one level up: the launch speed has to sit above the automatic's down-shift point,
  or the box downshifts the moment the clutch locks and a loaded truck on 12% hunts down to first.
- `--truckcheck` now pulls away at half throttle on full lock with 39 t.

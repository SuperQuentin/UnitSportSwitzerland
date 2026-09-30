# A replicated value whose MEANING depends on another replicated value must be reset when that one changes

- **A replicated value whose MEANING depends on another replicated value must be reset when that
  one changes.** `Anim` means gait speed + phase on foot and cadence + crank angle on a bike; the
  ride kind and `Anim` arrive in the same update, but the owner had not rewritten `Anim` yet on the
  frame the ride changed, so the bike read the rider's stride phase as a crank angle (0.93 rad, an
  instant snap). `ApplyRide` zeroes `Anim`/`BodyPose`/`PoseKind` with the kind.

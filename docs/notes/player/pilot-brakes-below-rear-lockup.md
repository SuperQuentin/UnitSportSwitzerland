# A scripted driver brakes below rear saturation

- **A scripted driver brakes below rear saturation** (`RaceLine.SpeedProfile`, #39). `Car.Step` splits
  the brakes 65/35 front/rear and braking moves load off the rear, so past
  `μg·a/L / (0.35 + μh/L)` (~10 m/s² for an R34) the rear circle is all brake and has no side grip
  left: with the speed cap gone, straight-line braking from 225 km/h swapped ends at 17° of slip on a
  line correction. The profile brakes at 80% of that (and of the published `BrakeDecel`), and the
  pilot eases the pedal once the rear steps out past 3°. Real, not scripted: the model has no ABS or
  proportioning valve, and neither does this driver's right foot.

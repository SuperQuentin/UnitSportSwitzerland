# A scripted driver brakes below rear saturation

- **A scripted driver brakes below rear saturation** (`RaceLine.SpeedProfile`, #39). `Car.Step` splits
  the brakes 65/35 front/rear and braking moves load off the rear, so past
  `μg·a/L / (0.35 + μh/L)` (~10 m/s² for an R34) the rear circle is all brake and has no side grip
  left: with the speed cap gone, straight-line braking from 225 km/h swapped ends at 17° of slip on a
  line correction. The profile brakes at 85% of the least of that, the tyres and the published
  `BrakeDecel` — the rear limit taken whole up to 110 km/h and at 80% from 215 km/h (80% everywhere
  cost 6% of lap time) — and the pilot eases the pedal once the rear steps out past 3°. Braking in a
  bend shares the tyres with the turn (friction circle). Real, not scripted: the model has no ABS or
  proportioning valve, and neither does this driver's right foot.

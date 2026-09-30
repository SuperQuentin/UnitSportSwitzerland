# Slipstream (draft)

- **Slipstream** (`RideGround.Draft`, `RideGround.DraftBehind`, `FootPlayer.Draft`; #52). A car or
  motorbike 2-25 m behind another vehicle, within ±15° of its own travel, level with it (±3 m) and
  going the same way at ≥ 10 m/s, pushes less air: drag × (1 − draft), draft = 0.45 at 2 m falling
  linearly to 0 at 25 m, the best of all vehicles ahead. Worked out in `FootPlayer.RidePhysics` from
  every other player's position and `WorldVelocity` (a remote's replicated velocity), so it is the
  same on every peer for the car that peer simulates; `Car.Step` and `Motorbike.Step` read it.
  Traffic (not players) gives no tow.
- Effect (`--spincheck` prints it): 1 km flat out from 150 km/h, AE86 192 km/h alone vs 203 km/h 10 m
  behind another car (draft 0.29), ZZW30 196 vs 207. A pack reads 0.2-0.3 behind the car ahead on
  the straights (`--drivecheck --trace` prints `draft=` every 0.1 s); the follower closes in, which is
  what lets a pass on a straight happen.

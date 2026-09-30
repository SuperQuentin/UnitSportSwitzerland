# Slipstream (draft)

- **Slipstream** (`RideGround.Draft`, `RideGround.DraftBehind`, `FootPlayer.Draft`; #52). A car or
  motorbike 2-25 m behind another vehicle, within ±15° of its own travel, level with it (±3 m) and
  going the same way at ≥ 10 m/s, pushes less air: drag × (1 − draft), draft = 0.45 at 2 m falling
  linearly to 0 at 25 m, the best of all vehicles ahead. Worked out in `FootPlayer.RidePhysics` from
  every other player's position and `WorldVelocity` (a remote's replicated velocity), so it is the
  same on every peer for the car that peer simulates; `Car.Step` and `Motorbike.Step` read it.
  Traffic (not players) gives no tow.
- Effect: at 200 km/h a 6-car pack reads draft 0.2-0.3 on the straights; the follower gains a few
  km/h and closes in, which is what lets the pilot's pass on a straight happen. It never raises a
  car's top speed alone.
- Check: `--drivecheck --trace` prints `draft=` per car every 0.1 s.

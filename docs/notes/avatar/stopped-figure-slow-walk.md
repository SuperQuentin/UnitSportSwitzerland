# A stopped figure is not a slow walk

- **A stopped figure is not a slow walk.** `HumanMeshBuilder.Cadence` has a floor — it must, or a
  figure inching forward takes one step a minute — and that floor keeps the legs turning over
  when the body has stopped. Everything the gait displaces is scaled by a `moving` factor that
  reaches zero at 0.25 m/s, and `AdvancePhase` freezes below it. Second half of the same bug:
  `RacePlayback` passed the real frame delta to its runners **while paused**, so a paused replay
  ran on the spot.

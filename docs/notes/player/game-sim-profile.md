# Game / Sim profile

- **Game / Sim profile** (`GameSettings.RideProfile`, Settings → Movement, `--profile game|sim`,
  default Game; `Rideable.Arcade`). Game is an arcade layer on the SAME equations: bike 350/900 W,
  0.88 rad lean, harder brakes; skis deeper edges, half the carve scrub, faster skating; running
  5.8 m/s. Sim is the untouched real-world model, the only one where `Bicycle.RiderWatts` (the
  home-trainer input) means anything. `--ride` forces Sim unless `--profile` is given, so its
  reference numbers (180 W → 32.7 km/h) stay checkable.

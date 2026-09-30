# Steering by lean

- **Steering by lean** (`Rideable.SteerByLean`): the input sets a target bank that eases in over
  ~0.2 s (out 1.6x faster) and the yaw rate is what that bank sustains, `g·tanφ/v`. Setting the yaw
  rate straight from the input made every correction a jerk — the "stiff" feel. Ski edge scrub is
  quadratic in bank, so a moderate carve holds speed. The chase camera trails the turn
  (`_turnLag` ∝ yaw rate) instead of being bolted behind the rider.

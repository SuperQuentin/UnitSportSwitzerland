# Feeding collision back into a vehicle needs `GetRealVelocity`, and a threshold

- **Feeding collision back into a vehicle needs `GetRealVelocity`, and a threshold.** Two wrong
  versions came first. (1) `Velocity` after `MoveAndSlide` is *projected along whatever you hit*,
  and against a slope too steep to climb that projection points up the face and keeps most of its
  magnitude — a skier jammed against a bank reported 22 km/h while its position had not changed
  for twelve seconds. (2) Clamping to `GetRealVelocity` every frame then killed the bike, because
  the ground is a 2 m lattice and crossing each bump costs a little forward motion *every frame*;
  compounded, that bled a bike from 107 m of riding to 11 m on flat ground. Only a shortfall
  that **persists** (smoothed, and past `ImpactTolerance`) is an impact. The **shortfall** (commanded minus achieved speed) is what is smoothed, not the achieved speed:
  a smoothed speed lags a launch by `a / ImpactResponse`, so every acceleration past 6 m/s² read
  as a wall (a motorbike did 0-100 in 5.2 s instead of 3.3; #38).

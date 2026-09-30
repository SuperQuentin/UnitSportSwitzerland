# Tricks, landings, boost

- **Tricks, landings, boost** (mounted, `FootPlayer`): hold **Trick (F / RB)** in the air and the
  stick flips (`_airPitch`) and spins (`_airSpin`) the rider+machine VISUAL — the body keeps its
  heading. Released, leftover rotation eases to the nearest whole turn. `GradeLanding` (air > 0.3 s)
  grades the residual angle: < 0.5 rad clean (named trick, speed kick, boost), < 1.1 sloppy (−45%
  speed), else bail (stopped, 1.2 s on the ground). **Boost (Q / LB)**, Game only: +7 m/s² while the
  meter lasts (0.4/s); filled by clean air and tricks. `Announced(text, good)` drives the popup +
  chime in `PlayerFeel`. The visual is rotated about a pivot 0.9 m up, not its origin at the
  contact patch, or a flip swings the bike through the ground.

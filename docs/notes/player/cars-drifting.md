# Cars and drifting

- **Cars and drifting** (`Player/Car.cs`, `CarSpec`, `RideKind` 8–10: Coupe 86, Rotary FD, Rally 4WD;
  issue #1). A car is the one mount that does not go where it points, so `RideMotion` gained **`Slip`**
  (travel minus nose, rad, + = left; π reversing) and `FootPlayer.RidePhysics` moves the body along
  `Yaw + Slip` — zero for every other mount, which is why nothing else changed. The model is a planar
  bicycle model in `RideMotion` alone (speed, slip, yaw rate), so a wall, boost or a sloppy landing that
  edits `Speed` applies to the car too: slip angles through `sin(C·atan(B·α))` (peak ~0.15 rad), each
  axle's side force limited to what its **friction circle** leaves after drive/brake force, load
  transfer from the last step's acceleration, 5-speed auto box, 4 substeps. Every way into a drift
  falls out of that: **handbrake** (Space / A — `Rideable.CanHop` false, `RideInput.Handbrake`) collapses
  the rear circle, power-over eats it, and braking into a turn unloads the rear (feint). Game adds grip,
  power, a counter-steer assist and a **yaw moment that catches the car past ~35°** (the fronts are on
  the lock stop by then, so steering alone cannot); Sim has none of it. Two traps found by the check:
  the low-speed kinematic blend must key on TOTAL speed (keyed on forward speed it zeroed the sideways
  speed at 70° of angle, 50 km/h gone in 0.3 s), and speed-scaled steering lock must lift in a slide or
  there is not enough counter-steer to catch anything. The chase camera swings ~55% toward the travel.
  Known limits: the body is still the player capsule (radius 0.85 m); per-surface grip for cars came with
  the presets (`car-setups`, #40). Check: `<godot> --headless --path . -- --driftcheck
  [--trace]` — flat ground, no world: launch, handbrake entry, 4 s hold, recovery for every car in both
  profiles; non-zero exit on a spin, no drift, or no recovery.

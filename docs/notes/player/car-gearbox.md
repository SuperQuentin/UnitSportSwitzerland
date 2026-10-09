# Car gearbox: automatic, sequential, manual (#290)

- **Setting**: Settings → Vehicles → "Car gearbox" (`GameSettings.CarGearbox`, `--cargearbox auto|seq|manual`).
  Only the local driver's car takes it (`FootPlayer.PrepareCar` → `Car.SetGearbox` every step); NPC,
  traffic, a script's `RideControls` and a kart (centrifugal clutch) stay `Automatic`. Nothing is
  replicated: the box is the owner's, the pose already carries the rpm.
- **Automatic**: unchanged (`Car.Step`'s own box; the brake at a standstill is reverse), unless a
  wheel's H-shifter is bound: then the lever is a **P R N D selector** (`HeldShifter.Selector`, the
  user's layout): gate 1 P, gate 3 and the R gate R, out of every gate N, every other gate D (4 above
  all; no gear limit per gate). `Car.Selector`, set by `FootPlayer.PrepareCar`: D never backs up on
  the brake, R is driven on the gas and is N while rolling forward faster than 1 m/s, N revs in place,
  P is N plus the brake once below 1.5 m/s (a pawl ratchets past faster). HUD: `Car.GearText` (P, R,
  N, D3). Keyboard and pad keep the pedal-picked reverse. Parked, the selector is cleared.
- **Sequential** (`Car.Gearbox.cs`): the driver shifts with `shift_up` / `shift_down`; the automated
  clutch needs no pedal and never stalls. It drives on the automatic's model (engine speed = the
  wheels' through the gearing, floored at idle: the clutch slipping from a standstill), with a 0.18 s
  cut on each shift. R ← N ← 1 → 2…; N from first only below 1 m/s, R only below 1 m/s. Neutral revs on
  the throttle. Reverse is driven on the gas, not the brake.
- **Manual**: the gates (`gear_1`..`gear_6`, `gear_r`, `gear_n`, or a wheel's H-shifter) and
  sequential shifts both need the clutch past 75% (else "GRIND", nothing changes); a gate past the
  car's gears is "No such gear"; a gear that would put the engine past 105% of the redline is refused
  ("Too fast for that gear"). Into the manual box at a standstill the car is in neutral.
  - **The engine is a flywheel** (0.1 kg·m²) against a dry clutch, as the trucks' (`HeavyDriveline`):
    the pedal bites between 65% and 25% of its travel, capacity 1.5x the curve's peak, slip until the
    engine's and the wheels' speeds meet, then locked.
  - **Closed-throttle drag** (friction + pumping, `peak · (0.15 + 0.2 · rpm/redline)`, only the part
    the throttle does not cover): from the limiter to idle in ~1.5 s in neutral, and engine braking in
    gear (AE86 in second from 70 km/h: 9.4 km/h lost in 2 s against 1.8 coasting). The published curve
    is net, so at full throttle there is no drag on top.
  - **Progressive throttle**: torque share = 1 − (1 − pedal)², as a throttle body gives most of the
    torque in its first third at low revs. Without it six cars (EG6, NSX, MR2, NA6, ER34, Prius) stalled
    pulling away on a third of the pedal.
  - **Idle governor** up to 0.45 of the peak, paying its own drag, so idle is the spec's.
  - **Stall**: in gear, the clutch over 0.3 bitten and the engine under half its idle. `Car.Stalled`
    → `FootPlayer.AfterCarStep` switches the ignition off (`EngineToggled`, as a truck's stall). With
    the ignition off the manual car's engine only drags: nothing drives until it is restarted.
    (Automatic and sequential cars still ignore the ignition for driving, as before.)
- **Clutch**: `clutch` held (C / pad B) goes down at 6/s and comes up at 1.7/s; a wheel's clutch pedal
  is its travel as it is (`ClutchFoot`, `PlayerInput.WheelPedal`). The cockpit's clutch pedal
  follows (`CarRig.Clutch`). Automatic and sequential: the pedal reads 0.
- **Pad**: in a car that is not automatic the shoulders shift (they are `shift_up` / `shift_down` in
  every vehicle) and do not trick or boost (`FootPlayer.ShouldersShift`, by `LastDevice == Gamepad`;
  F and Q still do). The VR grips replay as the shoulders. Gates are keyboard or wheel only, as on the
  trucks; VR gap: no car gear lever (`xr/vr-action-map`).
- **H-shifter**: `steering-wheel` (the lever is polled by `HeldShifter`: out of the gate is neutral).
- **Check**: `--cargearcheck [trace]` (`CarGearboxCheck`, headless, no world): every car's clutch
  launch (slips at the bite, no stall) and a stall dropped in third; the manual and sequential boxes'
  rules; engine braking; the shifter's lever; a kart stays automatic.

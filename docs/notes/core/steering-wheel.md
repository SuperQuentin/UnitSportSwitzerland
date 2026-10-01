# Steering wheel

- **Steering wheel** (`Core/SteeringWheel`, `WheelSettings`, `WheelPresets`, `Ui/WheelPanel`; issue #68):
  sim-racing wheels and pedals are read through **SDL3** (NuGet `ppy.SDL3-CS`, native libs for
  Windows/Linux/macOS land in `runtimes/<rid>/native`; `dotnet publish -r win-x64` puts `SDL3.dll` next
  to the game), not Godot's joypad API: that caps a device at ten axes, cannot tell which end a pedal
  rests at, and has no force feedback (see Force feedback below).
  Not installed on a headless run; SDL failing to load leaves the game without wheel support, nothing else.
- **What the game gets**, all through `PlayerInput`: `WheelAngle(lockToLock)` (radians, + right, NaN
  without a wheel or while keys/stick steer) goes into `RideInput.WheelAngle`; `Steer` falls back to
  the wheel over ±90° for mounts without a `Rideable.WheelLock` (bike, skis, planes); `Strength` of
  throttle/brake/`clutch` is the max of the bindings and the pedal; `WheelHandbrake` (lever or a
  button bound to `handbrake`) ORs into the car's handbrake. Wheel buttons are raised as
  `InputEventAction`s, so a bound button is that action everywhere a key is.
- **Direct steering** (`Car.Step`, `Truck.Step`): with a wheel angle the road wheels are
  `−angle / ratio` clamped to `MaxSteer`, and the keyboard helpers are skipped — rack easing,
  speed-scaled lock and the Game counter-steer assist. Game grip, power and the yaw catch past 35°
  stay. A car's ratio is `CarSpec.SteerRatio`, **derived** from `LockTurns` (turns lock to lock, half
  of it over `MaxSteer`), so the cockpit wheel (#69, `WheelTurn = SteerAngle · SteerRatio`) shows
  the real wheel's angle. Only three `LockTurns` are published figures (BNR32 2.7, CT9A 2.2, NA6CE
  2.9); the rest are marked `est.` in `CarCatalog`. Trucks and buses use the cab's
  `HeavyCockpit.SteerRatio` (20:1): ~1800° lock to lock for a 0.78 rad box.
- **Range**: `WheelSettings.RangeDeg` (270–1800°) must match the wheel's driver. 1:1 when the range
  covers the vehicle's lock; a smaller range is stretched over the lock so full lock is reachable
  (900° wheel in the 1260° AE86: 1.4x), and then there is no soft lock: the wheel's own stop is the lock.
- **Godot sees the wheel too** and would feed it into the pad bindings (wheel strafing, a released
  pedal read as a stick held back). The claimed device (matched by USB vendor/product, else name) is
  passed to `PlayerInput.SetIgnoredJoypads`, which rewrites every action's device −1 pad events as one
  copy per *other* connected pad, rebuilt on every connection change, and restores the −1 events
  when the set empties.
- **Bindings**: raw SDL axis/button indices per device, saved in `settings.json` under `wheel`. A
  preset (G29/G923 from Logitech's documented layout; HORI Truck Control System recorded on the
  device: steer 0, clutch/brake/gas 4/5/6 resting at −1, 54 buttons, 1 hat) applies when
  a new device is claimed; pedal rest ends are flipped from `SDL_GetJoystickAxisInitialState` if the
  preset had them backwards. **Until a wheel sends its first report every SDL axis reads 0** (the HORI's
  pedals rest at −1, so 0 is half pressed): a pedal reads 0 until its axis has read non-zero once.
  Settings → **Wheel** tab: device picker, range, live bars, and
  **Assign** = move the control (turn right / press the pedal / press the button); an axis resting
  mid-travel binds as half of a combined gas/brake axis. Hats are buttons from `HatBase` (1000).
- Gearbox: the trucks (#70) shift from `shift_up`/`shift_down`/`gear_*`/`clutch` and a wheel pedal holds `clutch` past half way; binding paddles and the H-shifter, and an analog clutch, are a follow-up.
- **Force feedback** (`SteeringWheel.Force.cs`, `WheelFeel`): SDL3 haptics on the claimed wheel. Each step
  `Car`/`Truck` set `Rideable.Feel`: **aligning torque** = steered axle's side force × trail (pneumatic
  0.035 m falling to 0 by 0.3 rad of slip, plus 0.02 m caster) over what that axle gives at its tarmac
  peak, so the wheel goes light past peak slip and on ice; faded out below 3 m/s. **Road** = surface
  roughness at speed (`CarSetups.Roughness`) plus a little tarmac texture, ~speed/0.5 m Hz. **Weight** =
  parked stiffness (unassisted `CarSpec.PowerSteering = false` racks 0.8, assisted 0.25, trucks 0.45),
  gone by 6 m/s. `HeavyTrain` keeps each axle's `AxleFy`/`AxleAlpha` for the trucks. `PlayerFeel`
  calls `SteeringWheel.Drive(feel, WheelLock)` every frame the local vehicle is on screen and
  `Knock` on `Impacted`/`Landed`; a feel older than 0.25 s leaves only a light damper.
- Effects: one infinite **constant** (aligning + **soft lock**, full within 8° past the vehicle's lock),
  a **sine** for the road, a one-shot sine for knocks, **damper** and **friction** conditions (the
  wheel runs those itself). All on `SDL_HAPTIC_STEERING_AXIS`; the wheel's autocentre is switched off
  where supported. **SDL gives the side a force comes FROM**: a positive level pushes the wheel left, so
  the game's + right is sent negated — found with `--ffbcheck` on the HORI (all five effects supported,
  features 0xd87ff, no autocentre control). `FfbInvert` flips it again for an odd device. Gains in
  Settings → Wheel (strength 70% default; 30% turns a free HORI rim ~80° in 0.5 s).

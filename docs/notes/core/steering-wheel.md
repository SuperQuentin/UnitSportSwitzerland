# Steering wheel

- **Steering wheel** (`Core/SteeringWheel`, `WheelSettings`, `WheelPresets`, `WheelPanel`; issue #68):
  sim-racing wheels and pedals are read through **SDL3** (NuGet `ppy.SDL3-CS`, native libs for
  Windows/Linux/macOS land in `runtimes/<rid>/native`; `dotnet publish -r win-x64` puts `SDL3.dll` next
  to the game), not Godot's joypad API: that caps a device at ten axes, cannot tell which end a pedal
  rests at, and has no force feedback. Joystick subsystem only for now (force feedback is PR 2).
  Not installed on a headless run; SDL failing to load leaves the game without wheel support, nothing else.
- **What the game gets**, all through `PlayerInput`: `WheelAngle(lockToLock)` (radians, + right, NaN
  without a wheel or while keys/stick steer) goes into `RideInput.WheelAngle`; `Steer` falls back to
  the wheel over ±90° for mounts without a `Rideable.WheelLock` (bike, skis, planes); `Strength` of
  throttle/brake/`clutch` is the max of the bindings and the pedal; `WheelHandbrake` (lever or a
  button bound to `handbrake`) ORs into the car's handbrake. Wheel buttons are raised as
  `InputEventAction`s, so a bound button is that action everywhere a key is.
- **Direct steering** (`Car.Step`): with a wheel angle the road wheels are `−angle / SteeringRatio`
  clamped to `MaxSteer`, and the keyboard helpers are skipped — rack easing, speed-scaled lock and
  the Game counter-steer assist. Game grip, power and the yaw catch past 35° stay. `SteeringRatio` =
  half of `CarSpec.LockTurns` (turns lock to lock) over `MaxSteer`; `Car.SteeringWheelAngle` is what
  a cockpit wheel (#69) shows. Only three `LockTurns` are published figures (BNR32 2.7, CT9A 2.2,
  NA6CE 2.9); the rest are marked `est.` in `CarCatalog`.
- **Range**: `WheelSettings.RangeDeg` (270–1800°) must match the wheel's driver. 1:1 when the range
  covers the vehicle's lock; a smaller range is stretched over the lock so full lock is reachable
  (900° wheel in the 1260° AE86: 1.4x). Soft lock at the vehicle's lock comes with force feedback.
- **Godot sees the wheel too** and would feed it into the pad bindings (wheel strafing, a released
  pedal read as a stick held back). The claimed device (matched by USB vendor/product, else name) is
  passed to `PlayerInput.SetIgnoredJoypads`, which rewrites every action's device −1 pad events as one
  copy per *other* connected pad, rebuilt on every connection change, and restores the −1 events
  when the set empties.
- **Bindings**: raw SDL axis/button indices per device, saved in `settings.json` under `wheel`. A
  preset (G29/G923, HORI truck wheel — both first guesses until checked on the hardware) applies when
  a new device is claimed; pedal rest ends are flipped from `SDL_GetJoystickAxisInitialState` if the
  preset had them backwards. Settings → Steering wheel: device picker, range, live bars, and
  **Assign** = move the control (turn right / press the pedal / press the button); an axis resting
  mid-travel binds as half of a combined gas/brake axis. Hats are buttons from `HatBase` (1000).
- Gearbox (paddles, H-shifter, clutch) waits for the trucks (#70), which add those actions.

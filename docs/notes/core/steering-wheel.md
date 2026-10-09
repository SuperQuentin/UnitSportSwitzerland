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
- **On foot only** (`FootPlayer`, `_ride == null && RidingWith == 0`): `WheelLookRate` turns the view
  (±90° = a full right stick, 5% centre deadzone) and `WheelWalk` (brake − throttle) walks back/forward.
  Mounted or seated, the wheel and pedals never touch the view. **Pedals turning the view** means Godot
  is reading the wheel as a pad: it was not claimed, or its Godot copy was not matched (the log prints
  `[wheel] Godot has no joypad matching ...` with every Godot pad's name and GUID). A saved `Device`
  that is not plugged in no longer blocks claiming the wheel that is.
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
- **Logitech G29 on hardware** (046d:c24f, PC mode, G HUB; with the Driving Force Shifter): 4 axes, 25
  buttons, 1 hat, the shifter reporting through the wheel base. The preset's documented layout is right:
  gas/brake/clutch axes 1/2/3 resting at +1 and −1 fully pressed, right/left paddle buttons 4/5,
  shifter 1–6 and R buttons 12–18, **held while in gear and released in neutral**. `--ffbcheck` PASS:
  push right +34°, push left −50°, a 35% push into the 60° soft lock held at 65° (no overshoot).
- **Gearbox** (#290): the paddles are `shift_up`/`shift_down`, the H-shifter's gates `gear_1`..`gear_6`/`gear_r`
  (G29 preset version 2; Settings → Wheel → Gearbox to assign them on any wheel). **The lever is the
  gear**: `SteeringWheel.ShifterGate` is the gate whose bound button is held (0 none, null with no gate
  bound), polled each step by `Player/HeldShifter` for the truck and the manual car: out of the gate is
  neutral; a gate pushed without the clutch grinds once and goes in, silently, as the clutch goes down.
  The wheel's raised `gear_*` events are swallowed (`FootPlayer.ShifterEvent`) so a gate is not picked
  twice. **The clutch pedal is travel**, not held past half way: `PlayerInput.WheelPedal(clutch)` into
  `HeavyDriveline.ClutchFoot` / `Car.ClutchFoot`, followed as it is; keys stay `HeldButton` with their
  own pace. The cars' boxes: `player/car-gearbox`. **On an automatic** (car or truck) the lever is a
  P R N D selector instead: gate 1 P, 3 and R reverse, out of a gate N, the rest D (`DriveSelector`).
- **Preset versions**: `WheelPresets.Preset.Version`, saved as `WheelSettings.PresetVersion`. Bindings
  saved from an older version gain each new button on the next claim (`WheelPresets.Upgrade`), unless
  that button or that action is already bound elsewhere.
- **Force feedback** (`SteeringWheel.Force.cs`, `WheelFeel`): SDL3 haptics on the claimed wheel. Each step
  `Car`/`Truck` set `Rideable.Feel`: **aligning torque** = steered axle's side force × trail (pneumatic
  0.035 m falling to 0 by 0.3 rad of slip, plus 0.02 m caster) over what that axle gives at its tarmac
  peak, so the wheel goes light past peak slip and on ice; faded out below 3 m/s. **Road** = surface
  roughness at speed (`CarSetups.Roughness`) plus a little tarmac texture, ~speed/0.5 m Hz. **Weight** =
  parked stiffness (unassisted `CarSpec.PowerSteering = false` racks 0.8, assisted 0.25, trucks 0.45),
  gone by 6 m/s. `HeavyTrain` keeps each axle's `AxleFy`/`AxleAlpha` for the trucks. `PlayerFeel`
  calls `SteeringWheel.Drive(feel, WheelLock)` every frame the local vehicle is on screen and
  `Knock` on `Impacted`/`Landed`; a feel older than 0.25 s leaves only a light damper.
- Effects: one infinite **constant** (aligning + **soft lock**, full within 20° past the vehicle's lock, with a
  light rim-speed damping past it, and the wheel's own **damper** raised to 70% from 6° short of the lock),
  a **sine** for the road, a one-shot sine for knocks, **damper** and **friction** conditions (the
  wheel runs those itself). All on `SDL_HAPTIC_STEERING_AXIS`; the wheel's autocentre is switched off
  where supported. **SDL gives the side a force comes FROM**: a positive level pushes the wheel left, so
  the game's + right is sent negated — found with `--ffbcheck` on the HORI (all five effects supported,
  features 0xd87ff, no autocentre control). `FfbInvert` flips it again for an odd device. Gains in
  Settings → Wheel (strength 70% default; 30% turns a free HORI rim ~80° in 0.5 s).
- **Soft lock bounced** at first: full force within 8°, updated at the frame rate, threw a free HORI rim
  88° → 40° → 77° (no hands on it). A 20° ramp plus the device-side damper near the lock (it runs at the
  wheel's own rate) holds it: `--ffbcheck`'s soft-lock stage pushes 35% into a 60° lock and must stop
  there (HORI: peak 84°, settles 60–74°, still a ±10° wobble with no hands). In a truck at 1800° the lock
  (~1790°) is the wheel's own stop, so there is nothing to feel; a car (AE86 1260°) has 270° of soft lock
  either side.
- **Low speed**: below 4 m/s the car blends to kinematic steering, and the feel blends with it (front
  mass × u × kinematic yaw rate, not the tyre curve at noise-sized slip angles): a parked wheel pulled
  back harder the further it turned, −0.5 at 2 m/s, and hid the soft lock. Now nothing to 1 m/s.
- **Soft lock ramp per wheel** (#290): `WheelSettings.SoftLockRampDeg` (3–30°, Settings → Wheel), the
  degrees past the lock to full force. The HORI keeps 20° (it bounced at 8°); the G29 preset sets 6°
  (version 3; `Upgrade` gives it to saved settings still at the 20° default). At 20° a kart's ±99° lock
  was not felt on the G29 (7° past it: 0.54). `--ffbcheck` on the G29 at 6°: a 35% push into a 60° lock
  stops at 61-63° and settles in 0.3 s (65° at 20°).
- **Forces silent at launch until toggled** (G29, #290): something resets the wheel after the effects
  are made (G HUB switching profiles as the window comes to the front, or Godot's own SDL opening the
  device while the world loads, not proven which) and every update still succeeds, so `Send` has nothing
  to recover. `Refresh` destroys and remakes the effects **on the open device** when a drive starts after
  2 s without a feel and on `NotificationApplicationFocusIn`; the log says `force feedback made afresh`.
  **Never close and reopen at once**: Windows refuses the reopen ("SDL_SYS_HapticOpenFromJoystick
  failed") and the wheel had no forces at all. A failed open now retries every second, 5 times
  (`OpenHaptic`), which also covers `RecoverHaptic`'s reopen. `--ffbcheck` PASS with the refresh.
- **Soft lock at full device force**, whatever `FfbStrength`: capped at 70% a hand pushed 121° through it.
- **Engine** (`WheelFeel.EngineFrom`, added by `PlayerFeel`, which knows `EngineOn`): a sine at the
  crank's rate (rpm/60, 8–60 Hz), 0.15 at idle to 0.5 at the redline, gain `FfbEngine`. **Road** is
  roughness only, half scale, nothing on tarmac (it grew with speed everywhere and reached 0.8 on gravel).
- **Godot must never touch the wheel**: `PlayerInput.Install` runs again when a world loads, and its
  `RegisterActions` put the every-device pad bindings back; past the stick deadzone (180° of an 1800°
  wheel) the wheel steered as a stick (eased, from centre: a "snap"). `Install` retargets again.
  `PlayerInput.Rumble` skips the ignored pads too: Godot rumbles a force-feedback wheel through its own
  SDL, which can take the forces away. Refused effect updates are logged, restarted, and the device
  reopened (`Send`/`RecoverHaptic`).
- Tools: `--ffbcheck` (direction + soft-lock push, hardware), `--wheellock deg` (every vehicle's lock to
  lock, steering ratio and cockpit wheel follow), `--ffblog` (what the wheel is given every 2 s).

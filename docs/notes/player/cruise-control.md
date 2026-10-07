# Cruise control (speed regulator)

`Player/CruiseControl` (pure, `CruiseControlTests`) and `FootPlayer.Cruise.cs` (#494): a car, a
truck or bus, a farm machine (tractor, combine) or a motorbike holds a set forward speed.

- **Switch**: Y on the keyboard (the airliner's autopilot key; its own `cruise` action); on a pad
  D-pad → held 0.5 s (`PadHold`, as the airliner's autopilot; a tap of D-pad → is still the
  lights, now toggled on release in a car or truck); in VR hold R stick → (XrPad sends it as
  D-pad →), or the dash poke (`XrCabControls`: a car under the radio's, a truck right of the wheel).
- **Press**: off → on at the current speed, rounded to a whole km/h, 3 km/h at least (from a stop it
  creeps off at 3). On and within 2 km/h of the set speed → off. On and further off it (sped up with
  the throttle, or slowed) → that speed is the new one. There is no ±1 km/h step: one control
  works on all three devices.
- **Off by itself**: the brake pedal (> 0.1), the handbrake, rolling backwards, the engine off,
  leaving the driver's seat. A toast says "Cruise off"; the HUD shows `CRUISE <km/h>` while on
  (also on the cockpit HUD line).
- **Loop**: one PI output, throttle above zero and brake below (up to 0.5), so it settles on the
  speed downhill and on a stepless tractor that creeps on at idle. Kp 0.35 per m/s, Ki 0.15. It
  starts from no throttle, never the driver's: pressed while accelerating at 0.6, the tractor's
  lagging ratio carried it from 6 to 9.8 km/h before it came back. The driver's own throttle wins
  when it asks for more (the integral waits meanwhile) and it returns to the set speed after.
- **Applied** in `RidePhysics` after the bail / seat checks, before `Step`: client-side input, so
  nothing replicated and no protocol change. Not for NPCs, bikes, skis, boats or site machines.
- **Checked** in `--tractorcheck` (fixture `flat`): the tractor set at 6 km/h holds 5.8-6.0 with
  the pedals let go and 5.6-5.9 with the plough lowered under it; the brake switches it off. A car
  up the paved strip holds 60 (59.9-60.0), sped to 76 and pressed again holds 76.

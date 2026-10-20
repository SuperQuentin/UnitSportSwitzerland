# Snowboard (#922)

Status: done in PR #923 (`feat/922-snowboard`, stacked on #919).

A board mount on the ski physics (`SkiDynamics`, #899), riding what skis ride.

1. `SnowRide : Rideable`: the snow step shared by `Skis` and `Snowboard` (dynamics, slope
   ground, snow wear, lifts); `Rideable.OnSnow(kind)` replaces the ski-only checks.
2. Board tuning (`SkiDynamics.Tuning.Board`): tighter sidecut, toe-side edge grips more than the
   heel side, no plough; brake = heel-side skid stop; one-foot push slower than skating.
3. Switch: spinning past a quarter turn at speed swaps nose and tail, the rider kept facing.
4. Ollie on jump (the skis' hop).
5. Stance regular / goofy in Settings > Gameplay, mirroring the rider.
6. Lifts, slope races, HUD, map, autopilot, XR, interest and trailer actors treat it like skis.
7. Model viewer showcase; rows in `xr/vr-action-map` and `bots/bot-capability-map`; notes.
8. Checks: unit tests (board tuning, switch), quick tier, ride probe on a slope.

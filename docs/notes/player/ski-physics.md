# Ski physics: carve and skid, the fall line, the stride (#899)

- **Model**: `SkiDynamics`, pure, `SkiDynamicsTests`. `Skis.Step` only turns the heading and
  passes the ground. The skis' horizontal velocity is (u along, w across, + left), kept in
  `RideMotion.Speed` and `Slip`, as a drifting car's. When the heading turns, `Slip` takes the
  difference (`Slip -= Δyaw`); the velocity lags the skis.
- **Edges**:
  - **Carve**: the slip angle is under the window (Sim 0.12 rad, Game 0.22) and the skis are on
    edge. The grip, g·(GripBase + GripEdge)·edge·snow grip, turns w into u with no loss of speed.
  - **Skid**: otherwise w is scraped off at g·SkidMu·(0.5 + 0.5·edge), and the speed is lost: the brake.
  - Flat skis do not carve, so a traverse with flat skis slips down; set edges hold it.
- **Fall line**: `SkiDynamics.Slope(normal, heading)` = g·n.y·(n.x, n.z), split along and across
  the heading, gives `RideGround.SlopeAlong/Across`. The old `Grade` (along the heading only) is no
  longer read by skis.
- **Friction**: μ 0.05 (Sim) or 0.04 (Game) × the slope's snow (#887) on `Snow`/`Ice`; **0.30 off
  snow** (grass, asphalt, gravel). That stops a skier on anything gentler than about 16°.
  - `Surfaces` now calls snow everything above `CoverPalette.SnowLine` + 125 m, and the ground
    under a running occasion's snow (`DayNight.OccasionSnow`). So the white the player sees glides.
- **Stride** (the owner's call: off snow the skis barely move unless you press again and again):
  - a rise of the throttle past 0.5 is one push, at most one every 0.15 s; holding gives nothing;
  - on snow +1.1 m/s up to 4.5 m/s (Game 1.4 up to 5.5); off snow +0.7 up to 1.4 (Game 0.8 up to 1.6);
  - tapping six times a second off snow is walking pace (measured on grass at Riddes: 10 m in
    10 s), four is barely moving;
  - keyboard taps W; a pad taps RT or flicks the stick; VR's pole push is a pulse per push;
  - `AutoPilot` presses about four times a second; `--ride skis` presses six times a second.
- **Hockey stop**: brake + steer (both > 0.3) above 2 m/s yaws the skis at 3 rad/s whatever the
  speed, full edge, no plough. The slip grows and the skid scrubs it: from 14 m/s to a stop in
  about 2 s. Brake alone is the snowplough (3.5 m/s²).
- **Landing**: `LandSkis` takes the speed into the slope (the last airborne velocity against the
  floor normal) as an equivalent fall height, v²/2g:
  - under 1.5 m: clean;
  - to 3 m: STUMBLE, 25 % of the speed lost;
  - beyond: BAIL.
  - The trick grades (`GradeLanding`) still apply first.
- **Hints**: "Tap to skate" under 4 m/s; "Brake: snowplough" to 6; "Brake + turn: skid to a stop"
  faster; "Hold: schuss".
- **Feel (#933, playtest)**: turns were a snap. `MaxYawRate` 2.4 → 1.5 (Game 2.7 → 1.75),
  `BankResponse` 7 → 4.5 (Game 9 → 5.5). The plough decelerates 4.5 m/s² (Game 5.5); brake with a
  turn is a hockey stop only above 5 m/s (`PivotSpeed`), slower it is a plough turn. Shift is the
  schuss: steering x0.3 (`SchussSteer`) and the tuck's drag; the figure folds into the egg
  (`avatar/snow-rig`). The chase camera sits 5.4 m back and eases out 0.12 m per m/s, up to 3 m
  more (`Rideable.ChaseStretch`). First person turns with trick spins and flips (not in VR).
- **AutoPilot**: `MeasureLean` measures skis on snow and accepts their slip; it returned null
  for any slipping mount, which would have stopped skis being a lean mount.
- **Races on skis need snow**: off snow a ski race is a walk, so ski courses follow the slopes
  (#909, `world/races`). `tools/racecheck.sh skis` must be given a start on a slope, e.g.
  `2580350,1100050 600`.

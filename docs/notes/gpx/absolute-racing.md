# Absolute Racing

- **Absolute Racing** (`CameraMode.Racing`, after Cinema in the C cycle; `--racingmode`; #22, #30): the
  Absolute Cinema director (`Director.ForRacing()`) over a shot set of **camera drones**
  (`Cinema/RacingShots.cs`, MF Ghost style). Every shot is a `DroneShot`: a body with a velocity,
  capped at 200 km/h and ~2.5 g, flying at the shot's `Target` with feed-forward of the target's own
  motion; it follows the terrain (3.5 m AGL here and 0.8 s ahead) and climbs to regain line of sight.
  Because it flies straight at its target it takes shortcuts by itself: **apex cut** finds the sharpest
  corner 1.5-6 s ahead (`Runner.CourseAt`, `NextCorner`), starts at the chase position, flies the chord
  to hover 10 m up on the inside and watches the car slide round, then drops in behind. Also parallel
  tracking on the inside of the next bend, top-down, lead reverse (framing the chaser), a swoop from
  high ahead to behind, a wide duel shot framing both cars, and a drone chase as the fallback (last in
  the array). Nothing sits at ground level: the bumper/wheel/trackside rigs it replaced spent most of a
  descent filming grass. `ShotContext.Rival` is the nearest other runner; `Nose`/`Slip` are the body vs
  the travel — for a recorded car the body turns to the recorded yaw (`<us:yaw>`), so a drift reads as a
  drift. `CanSee` ignores tree trunks (layer 2): the sightline cut dissolves them, so a forest must not
  veto a vantage. The zoom bubble is off in this mode: a drone never lets the car get small. A
  recorded race from `--drivecheck --record` exported with
  `--racingmode --path 0 --export ...` is the way to film one.

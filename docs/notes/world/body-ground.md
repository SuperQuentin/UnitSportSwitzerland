# The ground on another body (#1850)

Under `--frame earth` with a body's pyramid (`--bodies DIR`, `DIR/<body>/cube`), the anchor's body has a near ground
you can stand on. Plan: `docs/plans/planets.md` sections 4, 5 and 7.

- **Heights**: `Terrain/Bodies/BodyHeights` (plain C#, `BodyHeightsTests`): the pyramid's finest level (the Moon's L5,
  167 m cells) bilinear, plus `CraterField` (2.5-320 m craters). A missing tile is the reference surface with its craters.
  `Grid` gives a square in the tangent frame at a point; `Coarse` is the globe's own surface (its stride-8 triangles).
- **The square**: `World/Bodies/BodyGround`, 256² cells of 2 m round the camera, under 25 km over the reference. A worker
  builds a new one when the camera is 96 m from the centre, swapped in whole (height map + mesh). The last 64 m ease into
  `Coarse`, and the globe's own tiles discard a square column over it (`hole_*` in `body_globe.gdshader`), so the two
  meet without a step. Placed from doubles once a re-anchor.
- **Everything that asks the ground**: off the Earth `EarthWorld.HeightAt`/`HasCollisionAt` forward to
  `EarthWorld.OtherBody` (`Terrain/IBodyGround`, the square). Stepping in (`ClientWorld.GroundUnderCamera`), the drop
  onto the ground and the rescue from the void work unchanged. Water, cover and tunnels are "none" there.
- **Gravity**: `Core/BodyGravity.Ground` (the walker's `Gravity`), set from `CelestialBody.GroundGravity` on
  `WorldOrigin.BodyChanged` by `BodyViews`; off the Earth the physics space's gravity too. Vehicles keep 9.81 (C3a).
- **Earth-only systems off the Earth**: guard on `Origin.Body` (`Traffic.RebuildIfMoved`, `DormantVehicles`), or a
  Moon place names an Earth tile and fills it.
- **Check**: `--frame earth --chunks fixture:straight --sun real --date 2027-07-15 --time 10 --occasion none --bodies
  E:/UnitSportSwitzerland/planets --mooncheck` (~75 s headless): the flight, then down, T, standing, a 120 m sprint over
  a new square, a 5.4 m jump. Windowed: `test_output/moon/{arrival,ground,apex}.png`. 2027-07-24 puts the landing near
  the terminator (low sun, the craters show).

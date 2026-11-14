# The ground on another body (#1850)

Under `--frame earth` with a body's pyramid (`--bodies DIR`, `DIR/<body>/cube`), the anchor's body has a near ground
you can stand on. Plan: `docs/plans/planets.md` sections 4, 5 and 7.

- **Heights**: `Terrain/Bodies/BodyHeights` (plain C#, `BodyHeightsTests`): the pyramid's finest level (the Moon's L5,
  167 m cells) bilinear, plus `CraterField` (2.5-320 m craters). A missing tile is the reference surface with its craters.
  `Grid` gives a square in the tangent frame at a point; `Coarse` is the globe's own surface (its stride-8 triangles).
- **Two squares** (#1850 follow-up): the near one below (solid) inside a medium one: 256² cells of 16 m (4 km, a
  walker's horizon is 2.4 km on the Moon, 3.4 on Mars), the craters from 40 m (`CraterField.At(smallest:)`), drawn only,
  rebuilt at 512 m. The near square's edge eases into the medium's ground (`BodyHeights.Grid(..., edge)`), the medium's
  into the globe's `Coarse`; the medium discards under the near square (its own `hole_*`), the globe's tiles under the
  medium (`BodyGround.Hole` is the outer square).
- **The near square**: `World/Bodies/BodyGround`, 256² cells of 2 m round the camera, under 25 km over the reference. A worker
  builds a new one when the camera is 96 m from the centre, swapped in whole (height map + mesh). The last 64 m ease into
  `Coarse`, and the globe's own tiles discard a square column over it (`hole_*` in `body_globe.gdshader`), so the two
  meet without a step. Placed from doubles once a re-anchor.
- **Everything that asks the ground**: off the Earth `EarthWorld.HeightAt`/`HasCollisionAt` forward to
  `EarthWorld.OtherBody` (`Terrain/IBodyGround`, the square). Stepping in (`ClientWorld.GroundUnderCamera`), the drop
  onto the ground and the rescue from the void work unchanged. Water, cover and tunnels are "none" there.
- **Gravity**: `Core/BodyGravity.Ground` (the walker's `Gravity`), set from `CelestialBody.GroundGravity` on
  `WorldOrigin.BodyChanged` by `BodyViews`, only for a landable body (crossing the Sun's sphere keeps the last); off the
  Earth the physics space's gravity too. Vehicles keep 9.81 (C3a).
- **One ground a body, the anchor's answers**: every landable body with a pyramid has its `BodyGround`; the one whose
  body the anchor is on claims `BodyGround.Active` and `EarthWorld.OtherBody`. `Covers` is the square's true ground
  (192 m each way), twice the 96 m at which the next is built.
- **The Earth's leaves answer nothing off the Earth** (`EarthWorld.Locate`, #1854): another body's place read as an
  Earth one found a leaf and its sea, cover, tunnels or pits there.
- **The void**: `FootPlayer.RescueFromVoid`'s "under −500 m with no height known" is Switzerland's floor, Earth only
  (Mars' ground is kilometres under its reference).
- **Earth-only systems off the Earth**: guard on `Origin.Body` (`Traffic.RebuildIfMoved`, `DormantVehicles`), or a
  Moon place names an Earth tile and fills it.
- **Check**: `--frame earth --chunks fixture:straight --sun real --date 2027-07-15 --time 10 --occasion none --bodies
  E:/UnitSportSwitzerland/planets --mooncheck [mars] [--fromorbit]` (~50 s headless for the Moon): the flight, then down,
  T, standing, a 120 m sprint over a new square, the body's jump (5.4 m on the Moon, 2.4 m on Mars). `mars` flies Earth →
  Sun → Mars at light speed (~860 game s, ~13 min headless); `--fromorbit` skips the flight (27 s). Windowed: `test_output/moon/{arrival,ground,apex}.png`. 2027-07-24 puts the landing near
  the terminator (low sun, the craters show).

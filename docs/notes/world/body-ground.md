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
- **Bodies of a few mm/s²** (Phobos 5.8, Deimos 2.5, #1853): their true pull, one value and straight down as everywhere
  (not the shape's own pull, Mars' tide or the fall-off with height). T drops the walker its 0.85 m in 17-25 s, a jump
  at 4.2 m/s climbs 1.5 km (Phobos) or 3.5 km (Deimos, under its 5.6 m/s escape speed) and comes down after 24 or 56
  minutes; a sprint launches the walker from any bump.
- **A square's colour on a small body** (#1853): the finest tile over the centre that holds the whole square (Phobos'
  finest, 2.2 km, is smaller than the 4 km square, whose edges read past it); and the globe's exposure follows into
  the squares once its six faces are in (a square made before kept the shader's 1.6: a bright patch).
- **One ground a body, the anchor's answers**: every landable body with a pyramid has its `BodyGround`; the one whose
  body the anchor is on claims `BodyGround.Active` and `EarthWorld.OtherBody`. `Covers` is the square's true ground
  (192 m each way), twice the 96 m at which the next is built.
- **The Earth's leaves answer nothing off the Earth** (`EarthWorld.Locate`, #1854): another body's place read as an
  Earth one found a leaf and its sea, cover, tunnels or pits there.
- **Bodies one cannot stand on** (#1854): `CelestialBody.FloorM` (a giant with air is flown into, down to
  `GasFloorM`, 500 km under its 1-bar level; the Sun stops the camera at 0.2 radii); the spectator is pushed back out along the vertical below it (`SpectatorCamera.KeepOverFloor`),
  and T over one is refused with a toast ("Jupiter is gas all the way down"; between the planets, the Sun's space:
  "No ground out here"). `--orbitonlycheck`.
- **The void**: `FootPlayer.RescueFromVoid`'s "under −500 m with no height known" is Switzerland's floor, Earth only
  (Mars' ground is kilometres under its reference).
- **Earth-only systems off the Earth**: guard on `Origin.Body` (`Traffic.RebuildIfMoved`, `DormantVehicles`), or a
  Moon place names an Earth tile and fills it.
- **Boulders round fresh craters** (#1849, `Terrain/Bodies/BoulderField`, `World/Bodies/BodyBoulders`): the craters of the
  `CraterField` 10 m and up and fresh (depth over diameter: `CraterField.Freshness` ≥ 0.55, ~7 % of them) throw
  0.3 m to D/15 boulders from the rim to two radii (0.3 a metre of diameter when fully fresh, most small), a function of
  the crater (every peer the same). Within 400 m of the camera while it is under 3 km over the ground, rebuilt after
  100 m, laid on the ground's height a third sunk and flattened, `RingRocks`' shapes in the body's grey (Mars' rust),
  solid spheres within 150 m. ~200 round a walker on the Moon or Mercury, a few dozen on Mars. `--mooncheck` checks
  them on well-cratered bodies (a ray down onto the nearest).
- **`/goto <body> <lat> <lon> [km]`** (#1850, `Core/BodyGoto`, `ClientWorld.GoToBody`): the fly camera to a place on any
  body (the chat line, offline; online it waits for C3b): the fly camera taken if on foot, the anchor relabelled to the body
  (nothing moves), the world shifted to the place's tangent frame, the camera over it (30 km over a body with ground by
  default, never under a body's floor), T steps in after flying down. Planetocentric latitude, east longitude (−180..360).
  `--gotocheck` (quick, no body data): the Apollo 15 site, Gale crater, back over Bern, an unknown body refused.
- **Check**: `--frame earth --chunks fixture:straight --sun real --date 2027-07-15 --time 10 --occasion none --bodies
  E:/UnitSportSwitzerland/planets --mooncheck [mars|phobos|deimos|...] [--fromorbit]` (~50 s headless for the Moon): the flight, then down,
  T, standing, a 120 m sprint over a new square, the body's jump (5.4 m on the Moon, 2.4 m on Mars). `mars` flies Earth →
  Sun → Mars at light speed (~860 game s, ~13 min headless); `--fromorbit` skips the flight (27 s). Windowed: `test_output/moon/{arrival,ground,apex}.png`. 2027-07-24 puts the landing near
  the terminator (low sun, the craters show). A small moon: the flight ends (and `--fromorbit` starts) well inside its
  sphere (12 km over Phobos, not 100), the settling waits for its slow fall, no sprint, and the jump is judged on its
  first 5 s (v t − g t²/2); ~40 s headless each.

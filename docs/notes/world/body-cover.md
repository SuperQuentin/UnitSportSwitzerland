# Cover, scatter and fine ground on other bodies (#2016)

What lies below another body's data past the near square (`docs/plans/planets.md` sections 4 and 5; the ground itself:
`world/body-ground`). Works on the shipped base alone (`assets/bodies/`) and with `--bodies E:/UnitSportSwitzerland/planets`.

- **Three squares** (`World/Bodies/BodyGround`): the near one (512 m of 2 m cells, solid), a **fine** one (2 km of 4 m
  cells, the craters from 10 m, moved at 256 m) and the medium one (4 km of 16 m cells, from 40 m). Built in that order;
  each drawn one discards under the one inside it (`hole_*`), the globe's tiles under the outermost.
- **No pop at an edge: a geomorph by the camera's distance** (`body_ground.gdshader`): a drawn square carries per vertex
  the ground round it (UV2.x its height less the square's, CUSTOM0 its normal: `BodyHeights.Grid(..., edgeY)`), and the
  vertex shader turns the one into the other from `morph_from` to `morph_to` metres from the camera (fine 512-768 into the
  medium's ground, medium 1024-1536 into the globe's `Coarse`). A square built again where the camera is draws what the
  last one did. A short static ease at the very edge (128 m fine, 256 m medium) stays for a camera far off a square's centre
  while the next one builds. The near square has none: its collision is its drawn ground.
- **Cover** (`Terrain/Bodies/BodyCover`, `shaders/common/body_cover.gdshaderinc`, `World/Bodies/BodyCoverLook`): the families
  (regolith, Mars, ice, sulphur, basalt, Titan; `cover_kind`). Per square vertex (COLOR): the fresh craters' rays
  (`Freshness` ≥ 0.6, floor 0.7, blanket to 1.6 radii, 8-14 rays to 5 radii, from 5 m near, 10 m fine, 40 m medium; rg the
  square's own, ba the ground round it's, morphed with the heights) and the dune sand (`Dunes.Sand`, the mask the dunes'
  relief uses). Both fade from 400 to 1100 m from the camera: Mars' dark sand ending at the medium edge drew a band on the
  horizon. Everything else is the shader's own on the squares and the anchor body's globe tiles alike, so it never ends at a
  square: the mottling (0.5-512 m value noise), Mars' ochre dust, Io's sulphur palette, Venus' flows, and the **polar ice**
  by latitude and season (`BodyCover.IceEdges`: Mars' seasonal caps ~55°N / ~52°S in each winter to the residual 80°N /
  85°S, linear in the subsolar latitude; Ganymede's frost from 45°), its edge ragged at 40 and 8 km and further out on
  pole-facing slopes.
- **Body-fixed noise, precise near the camera**: `cover_basis` turns world offsets into the body's axes from `cover_origin`,
  a point of the body's 4096 m lattice near the camera in world space; the noise repeats every 4096 m, so the origin's
  choice never shows. Written only when the camera crosses a lattice cell, the world re-anchors or the sun's latitude moves
  0.1° (`BodyCoverLook.Version`; `BodyGlobe` writes its own tiles on a change, `BodyGround` its squares).
- **Scatter** (`Terrain/Bodies/ScatterField`, `World/Bodies/BodyScatter`, `ScatterShapes`): seeded on a 3D grid of cells
  as the craters (no face seam), a share per field, radii by N(>r) ∝ r⁻². Mars: rocks 0.12-1.4 m (solid from 0.4 m within
  60 m, drawn within 180 m) by a thermal-inertia guess (`ThermalInertia`: dust mantles 40-150, sand ~250, rocky plains to
  700; `RockAbundance` Christensen-like) and pebbles 3-12 cm within 30 m; the regolith's pebbles (Moon, Mercury, Callisto,
  Phobos, Deimos); Europa's and Ganymede's ice blocks 0.25-2.5 m (solid boxes from 0.3 m within 90 m) in chaos patches;
  Titan's cobbles off the sand. Io and Venus none (the plan lists none). One MultiMesh a shape a field, rebuilt on a worker
  after the camera moved the field's `MoveM`, never per frame; dropped above 2.5 reaches over the ground. Laid on the drawn
  ground (`At(n)` within 160 m, the fine square's past it), a quarter sunk.
- **Lit as the ground** (`shaders/body_rock.gdshader`): the scene's directional light at the ground's strength
  (`BodyGround.SceneLight`), its shadows, lunar-Lambert on dusty rock, a sky fill on a body with air (0.18, Mars' shadow
  sides), an ice sheen. The boulders round fresh craters (`BodyBoulders`) take it too (they were unshaded).
- **Shapes** (`ScatterShapes`, "Space" in the model viewer): angular rocks (an icosphere cut by five seeded planes, flat
  base) and ice blocks (a jittered box, a chipped corner); Titan's cobbles are `RingRocks`. Godot's front faces wind
  clockwise seen from outside: the first build drew them inside out.
- **Collision**: rocks spheres a little inside their faces, ice blocks boxes, on layer 1 (the walker's and the vehicles'
  mask) in one `StaticBody3D` "Solid" a layer. The server builds none here (other bodies' server collision is #1975's);
  a bot would need `ScatterField.Near` round it on the server, the same stones (a function of the seed and the ground).
- **Check**: `--mooncheck [body] --fromorbit` logs the fine squares, the cover and each field's counts, then a ray down
  onto the nearest solid stone and a car-sized box set on it (`MoonCheck.Scatter`). Pictures: `--bodyshot
  mars,sun,-40,0.002g,6` (a height ending in `g` is over the ground).

# Earth curvature, drawn round the camera (#187)

- **The world is flat on the CPU and round where it is drawn.** Every world shader lowers what it
  draws by the Earth's drop at its flat distance from the camera, d² / 2R. Positions, physics, the
  wire, tiles, builders and `Position.Y` as altitude are untouched. Plan and why not tiles on WGS84:
  `docs/plans/spherical-world.md`. The true ellipsoid at altitude: `docs/plans/ellipsoid-handover.md` (#1430).
- **Apparent curvature**: R = 6,378,816 m (LV95's projection sphere) over 1 − k, k = 0.13 (normal
  refraction, what a photo shows): R_eff ≈ 7,332 km. 7 cm at 1 km, 6.8 m at 10 km, 246 m at 60 km,
  682 m at 100 km. One source: `src/Core/Curvature.cs` (plain C#, `CurvatureTests`).
- **Shaders**: `shaders/common/curvature.gdshaderinc` (included by `common/world.gdshaderinc` and
  the avatar body). A body writes `POSITION = CURVED_CLIP(PROJECTION_MATRIX * MODELVIEW_MATRIX * v, world_pos)`
  (flat, that is the old product bit for bit; `P * (MV * v)` rounds differently and speckled the
  Realistic grass), or `CURVED(view, world)` on a view-space position it works on further (road, lamps);
  a depth-buffer read is put back with `UNCURVED_FRAGMENT(world)` (the water's depth and refraction).
  - **Only `POSITION` curves.** `VERTEX` and `world_pos` stay flat: facet normals, `pattern_xz`,
    portal clipping, the sightline cut, the horizon cover, the far-tree ray tracing and the mist
    are as before. A fragment comparing itself with the depth buffer uses the drawn position
    (`CURVED_FRAGMENT(vec4(VERTEX, 1.0), world_pos)`: a fragment has no `MAIN_CAM_INV_VIEW_MATRIX`, `CAMERA_POSITION_WORLD` is the same point there), as the water does.
  - **Round the view's own main camera** (`MAIN_CAM_INV_VIEW_MATRIX`), not `world_cam_pos`: a
    shadow pass drops round the camera it shadows for, a mirror or portal round its own eye, and a
    preview viewport (character page) round its camera. With the global, a figure in a preview
    3 km from the game camera dropped 0.6 m.
  - **A new world shader that writes `POSITION` must call `CURVED`** (or it floats over the ground
    at distance). Curved: terrain, road (before its far lift), building, tree (3D and the
    `world_vertex_coords` far billboards), water, prop, path, avatar (figures, vehicles, aircraft),
    the bonfires (`hoehenfeuer`), the signal lamps' lens and bloom (`SignalLamps`).
  - **Flat on purpose**: interiors (only ever near), the Sechseläuten pyre, wake foam, snowfall and
    other camera-relative effects (near).
- **At altitude** (#1430 H1, `docs/plans/ellipsoid-handover.md`): `Styles/CameraGlobal` pushes
  `world_curvature` = `Curvature.DropPerM2At(camera height)`: refraction k = 0.13 up to 5 km,
  smoothstep to 0 at 20 km (the planet looks geometric from the stratosphere). And
  `world_curve_blend` = `SphereBlendAt`, 0 below 20 km, 1 from 50 km: `curve_offset` in the include
  blends the drop into the **exact sphere** through the camera's nadir (the flat distance read as arc
  length: drawn in by s − (R + y)·sin θ, down by (R + y)·(1 − cos θ)), worked camera-relative in floats
  (no R + y, 1 − cos θ as 2·sin²(θ/2), series near 0). `Curvature.OffsetAsShader` is the same recipe
  in C#: `CurvatureTests` hold it to a centimetre of doubles out to 1,000 km. Below 20 km the shader
  takes the drop's old path (blend 0: one branch). Also by height: the far plane reaches
  √(2Rh + h²) + 200 km above 5 km; the style's haze (`Environment.FogDensity`) thins above 3 km,
  exp(−(h − 3 km)/10 km), kept at a sixth so the edge of the terrain data stays hazy until the globe
  (H4); with the globe drawn (from 5 km, #1528) exp(−(h − 3 km)/2 km), near the aerosols' 1.2 km scale height,
  and the flat world's edges dissolve into it (`player/orbits`); the horizon blocks' cull margin uses the unrefracted drop. The spectator stops at
  `Curvature.SpectatorCeilingM` (50,000 km); the UFO flies an orbit above 60 km (`player/orbits`, #1480). Normals are not turned (only `POSITION`
  curves, as before): the lit styles shade far ground as flat. The server's rules keep the ground k.
- **The global** `world_curvature` (1/2R per m², 0 flat) is pushed by `Styles/CameraGlobal` when it
  changes. Its default is 0: the model viewer and anything without `CameraGlobal` draws flat.
  `Curvature.Drawn` turns the picture off: `--curvature 0` or the debug menu's "Earth curvature".
  The server's rules never read it.
- **Not dropped** (accepted, error angle d / 2R: 0.04° at 10 km, 0.12° at 30 km): StandardMaterial3D
  and Label3D far things (race gates, POI markers, thermal clouds, particles, name tags), car glass
  and lamps. Sun, north and the compass are unchanged (#1430's).
- **Culling**: a flat box culls dropped geometry along the top of the screen. Horizon blocks carry
  `ExtraCullMargin` = the drop at their farthest corner (483 m at a 60 km horizon,
  `HorizonLayer.CullMargin`, updated with `DistanceM`). Tiles need none (≤ ~1 px within 10 km).
- **The server sees what is drawn**: `Net/Interest.Clear` and `PhotoJudge` add the Earth's bulge
  between the two ends, `Curvature.Bulge(s, D)` = s·(D − s)/2R (D²/8R halfway: 61 m at 60 km,
  138 m at 90 km). Two players 100 m up over a level plain see each other at 60 km, not at 90.
  `HitGuard` (700 m), the alphorn echo (4 km), bots and traffic (≤ 250 m) are left flat (≤ 1.3 m).
- **Screen projections of far places** subtract `Curvature.Drop` before `UnprojectPosition`
  (`TargetMarker`: 61 m at 30 km). Short-range ones (BR pings, combat HUD) need not.
- **The floating origin**: the drop is measured from the camera, never the origin, so a shift
  changes nothing in it (`--originstress` pictures are unchanged).
- **Checks**: `CurvatureTests`, `PhotoJudgeTests`, `--interestcheck` (60 and 90 km cases); the
  reference screenshots (`general/regression-net`): with `--curvature 0` the branch matched `main`
  shot for shot; with it on, near shots and the model viewer stay within their noise floor.

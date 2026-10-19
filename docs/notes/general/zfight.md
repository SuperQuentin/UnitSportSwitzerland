# Z-fighting and flicker: two tests (#788)

Flickering faces have two causes, and each test catches what the other cannot.

## 1. Geometry: `ZFight` (headless, quick tier)

- `src/Core/ZFight.cs` (pure, `ZFightTests`): two triangles **facing the same way** (or either
  drawn both sides), **within 8 mm** of each other's plane (`MaxGap`), **overlapping by over
  1 cm²**, with **different looks** (material + vertex colour: two faces that draw the same pixels
  cannot flicker) and the **same depth class** (the road shader pulls roads, and their paint
  further, toward the eye: never a fight with the ground). The region is the overlap clipped to
  where the planes are within the gap, so shallow crossings count as a strip.
- `visibleOnly`: rays from the region out along its normal (Godot's front face is clockwise, so
  out is minus `(B-A)x(C-A)`), straight and tilted 50° four ways; hidden if all meet an opaque face.
  Glass (blended / alpha materials) hides nothing. Faces inside a closed body never flicker.
- `src/Core/ZFightScan.cs` reads the triangles of every visible `MeshInstance3D` /
  `MultiMeshInstance3D` under a node (headless works: the dummy renderer keeps the arrays).
  **But not a MultiMesh's instance transforms**: headless, every instance reads as identity, so a
  model's instanced parts stack at its origin and fight each other there (a traffic light's
  `SignalLamps/@MultiMeshInstance3D... / ...` pairs, 0 mm apart near (0, 0, 0): ~5100 of its
  ~6400 cm²). The baseline is headless, so those lines are in it; windowed, the same lights score
  ~53 000 cm², mostly the lenses' bloom quads over the housings. The `NEW` / `WORSE` verdict is
  printed after a model's last place line, so it reads as if it belonged to that pair.
- **`--zfightcheck`** (quick tier, ~75 s): every model viewer entry against
  `tests/zfight-baseline.txt` (cm² per model). A model over its line, or a new model that fights,
  fails. `--zfightcheck update` rewrites the baseline: do it after fixing faces, and commit it.
  `--zfightcheck <text>` narrows the run; `--zfighthidden` keeps the hidden faces.
  Not `--modelsonly`: any `--models*` flag starts the viewer instead.
- **Model viewer `--zfight`**: the fighting regions in magenta over the model, the count in the
  header, the worst places in the log (works in `--models,<dir>` PNG runs: before/after shots).
- **Debug menu (F9) "Z-fighting"** / `--debugview zfight`: the same within 40 m of the camera,
  rescanned every 15 m or 5 s, places logged with their `--at`. The horizon is skipped (it is
  discarded where real tiles draw).
- At first run (Oct 2026): 1086 of 1318 models fight; e.g. coloured floors at 0 mm in the Setra
  and Citaro, the Duro's rear bumper and bed rails; tile borders overlap a metre at 0 mm in
  different colours; buildings' bases lie flush on the ground.

## 2. Pixels: `Flicker` (windowed)

- **The PS1 vertex snap is gone** (#788): `retro_snap` (`shaders/common/retro.gdshaderinc`)
  moved each vertex up to half a snap pixel on screen, so faces centimetres apart traded places
  at grazing angles; it is a pass-through now (dither and colour steps stay). **Textures finer
  than the screen** still shimmer (the real style's roof tiles): geometry cannot see that.
- `FlickerShot` renders 16 frames while the camera creeps through `HOffset`/`VOffset`
  (2 mm a frame at 10 m) and paints magenta every pixel whose brightness turned back at least
  twice (`Flicker.Reversals`, pure, `FlickerTests`): an edge passing changes once, a fight or a
  shimmer goes A, B, A, B. Anything else that moves (animated HUD, wind) shows too.
- Model viewer: `--models,<dir> --flicker [--style ps1]`, one heat PNG per model and a
  `[flicker] <model>: N px` line. Debug menu: **Flicker shot** button, or `--debugview flicker`
  (after 30 s, with a `--shot-queue` line holding the camera), PNG in `user://flicker/`.
- Found: the Duro's bumper, Citaro stripes and skirt, the Setra's rear corners (#755); Riddes'
  roofs (not a z-fight: clay view 0.04 %). Fixed in `roof_tiles` (#793): the ridge length that
  seeds which tiles and where the moss is was an interpolated varying, and its last-bit wobble
  through `hash()` (`sin * 43758`) flipped whole tiles; it is `flat` and rounded now. The lips'
  2 cm bands are smoothed over a pixel and fade out by a 6 cm pixel (moiré on far faces).
  **A hash seeded from an interpolated varying flickers: make it `flat` or round it.**
- Left: shadow-map shimmer of the sun's cascades on ground and walls (a flicker shot at Riddes:
  0.71 % of the screen with shadows, 0.32 % without).

## Fixing

- Buses (#755, `BusMeshBuilder`): the wall's arch band rose over the belt band (two colours in
  both faces), the lining lay on the wall's inner face, the side walls' ends lay in the end walls'
  faces (now the end walls are full width and the side walls stop at them), things on the floor
  shared its underside, the driver's platform ran into the face wall; mirror glass 2 mm proud of
  its housing (`CockpitKit.Mirror`, every vehicle) is 1 cm. 49 000 -> 600 cm² on the Citaro,
  250 000 -> 1 500 on the Setra; flicker shots 3554 -> 121 px.

- All vehicles (#755): 1.80 M -> 0.09 M cm² (cars 923 k -> 42 k, rides 368 k -> 13 k, trucks and
  buses 196 k -> 15 k, aircraft 181 k -> 1 k, motorbikes 106 k -> 13 k, boats 22 k -> 3 k). The
  same few causes everywhere: a panel's end in another panel's face (stop it where the other
  starts), a slab's underside shared by what stands on it (sink the slab 3 cm), a box running
  through a bulkhead (end it at the bulkhead), lamps/plates/stripes 2-6 mm proud (1-1.2 cm), a
  tyre's face a few mm under the body side (cars' tyres at most 21.5 cm wide), the cars' splitter
  5 mm under the bumper, alternating canopy cells of one size (paraglider). The airliner rig's
  `Hinge()` used to set `Rotation`, dropping each surface's swept hinge basis: spoilers and flaps
  sat yawed off the wing in game (`AirlinerRig.RestOf`).
- The visibility rays start just past the nearer face of the pair (#755): a cover 1 cm over a
  fight hides it; started further out, a ray could begin inside a part a few mm off and call a
  visible fight hidden (the Setra's door leaves).

Lift an overlay ~1 cm proud (`meshscratch-boxes-render-inside-out`), merge coplanar parts, or
give one face the other's colour; rerun `--zfightcheck`, then `--zfightcheck update`.

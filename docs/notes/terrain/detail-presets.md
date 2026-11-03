# Detail presets: what each one draws (#1216)

`GameSettings.Detail` (Settings → Graphics → Detail; `VrDetail` in a headset, `--detail` / `--vrdetail`) picks a
`LodPolicy` (`LodPolicy.Create`) and, through `GameSettings.EffectiveDetail` (at most Low on low data, Very low kept),
two flags read across the game: `GameSettings.Lean` (Very low) and `Cheap` (Low or Very low). The cuts a Quest 2
needed (#1112) were keyed to the headset; #1216 made them this preset, on every device.

| | Very low | Low | Medium | High |
|---|---|---|---|---|
| Building cell → its boxes (`BuildingBoxFromM`) | 300 m | 600 m | 1000 m | 2000 m |
| Road markings to (`RoadPaintToM`) | 250 m | 500 m | 1000 m | all |
| Shadow cascades (`StyleKit.ShadowSplits`) | 1 | 2 | 4 | 4 |
| Lighter `_standalone` shaders, sparse far trees, 200 m horizon lattice | yes | | | |
| Roads in 125 m cells | yes | | | |
| Decor drawn to (`OccasionDecor.DrawnM`) | 150 m | 300 m | 600 m | all |
| Parked cars to (`DormantVehicles.DrawnM`) | 250 m | 250 m | 400 m | 400 m |
| Herd poses, bird budget | halved | halved | | |

- **Every preset:** the buildings of the tiles within `LodPolicy.CellRings` (those whose near edge is nearer than the
  box distance: 1 ring, 2 on High) are 125 m cells, each its own mesh or its boxes by the cell's 3D distance; a tile
  beyond is one mesh of boxes. Cells on every loaded tile were 2700 draws on a desktop at Medium, slower than the
  triangles they saved. Height-based detail (`LodPolicy.WithAltitude`, `--noverticallod` to measure without).
  Parked cars and decor are cut into the same cells, so a range counts from where they stand.
- **By renderer, not preset:** on the Mobile renderer (phones, the Quest, and the desktop's Cartoon runs) roads,
  buildings and water draw before the ground and trees last (`StyleKit.CoverFirst`), for a tiled GPU's early depth
  rejection.
- **Live:** a preset change sets the ranges on the tiles already drawn (`ChunkNode.SetRanges`) and re-shades the
  materials and the sun (`StyleKit.Restyle` also keys on `Lean` and the cascade count). Road cells, the horizon
  step, decor and parked-car ranges apply to what is built after.
- **Measured** (PC, Geneva 2500300/1118450, 1920x1080, vsync off, `--shot-queue` street `g1.7` and `g220`):
  Medium street 10.5-11.0 → 7.6-8.4 ms (12.8 M → 9.0 M triangles), aerial 11.4-11.9 → 7.9 ms; Low street
  7.9-8.3 → 6.8-6.9 ms; Very low 5.7-6.2 ms street, 7.7-8.2 ms aerial.

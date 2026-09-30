# Garage buildings and their roll-up doors (#56)

- **Data**: GWR GKLAS 1242 is `BuildingKind.Garage = 10` since `.bldg` v2 (same layout as v1,
  decoders take both; `NetworkChunkSource` deletes and refetches a cached `.bldg` older than v2).
  Riddes has 58. Rebuild: `--features-only --buildings <gpkg> --gwr <gwr data.sqlite>` — the GWR
  must be the canton's (`gwr_vs` for Riddes); the VD file matches 0 of 1,159 buildings and
  classifies everything Other. See `tools/gwr-classify-gklas-gkat`.
- **Facade** (`BuildingMeshBuilder`, `ps1_building.gdshader`): sheet-metal grey walls (0.66, in
  the range of the other kinds), no window grid, a **sign** over the door (a workshop-blue board
  with a light face, `garage_sign`, plain paint by day and lit at night like a window) and a
  0.5 m **painted stripe** under the eave (`garage_stripe`, rust red, shaded like the wall), which
  still reads as a line across the valley. Flags ride in UV2.y: 1 = sign face, 2 = garage wall
  (then UV = metres along/up and UV2.x = eave height).
  **Never magenta, never a magenta/black chequer**: the first version (near-black wall, hot-pink
  neon sign, pink/black chequer band) read in game exactly like an engine's missing-texture
  pattern, and the owner took it for a bug.
- **The output is not sRGB-encoded**: a vertex colour goes through `SrgbToLinear` and is shown
  as that linear value, so a "dark grey" 0.26 renders almost black. Pick building and prop
  colours by what they render as (0.6 for a mid grey), not by their sRGB value.
- **Drive-in bay** (`src/Interiors/GarageBay.cs`, pure functions of building + door so render,
  collision and terrain holes agree on every peer): `Plan` fits a room inset 0.15 m in the plan
  box (floor at the door sill, ceiling at min(top + 0.4, eave - 0.1) but >= top + 0.1, four inner
  walls, reveals lining the opening) and narrows/slides/lowers the door to it (>= 2.3 x 2.1 m).
  No bay, painted door kept, logged once `[garage] <tile> #i: no drive-in bay, <why>`, when the
  door is not square to / not on a box side, the room is < 2.4 x 4.5 m, or the roof does not
  cover the whole room (not a rectangle): 23 of the 58 around Riddes get one.
  `CutFacade` clips every wall triangle on the door plane against the 4 bands around the doorway
  (area conserved, `--garagehole`). The room is emitted in both windings (mesh and collision:
  solid from inside). Terrain: `HoleCells` (1 m quads under the room) are merged into the tile's
  holes in the tail of `ChunkManager.StartBuild`, which rebuilds the near surface and collision
  with them; an apron coloured like the ground refills the carved cells outside the walls.
  Collision-only builds compute doors too, or hole and wall would disagree.
- **Roll-up door** (`src/Vehicles/GarageDoors.cs`, client only, created next to `OccasionDecor`
  in `ClientWorld`): one `GarageDoor_<building index>` node per garage door, a child of the tile's
  `ChunkNode` (unloads with it). `Leaf` = slatted door hung from the lintel, rolled up by
  squashing its Y scale over 1 s, a `StaticBody3D` box while shut (disabled once Open > 0.02);
  `Bay` decor (tool wall, red roll cab, neon tube) on the back/side inner walls. Opens while any
  `FootPlayer` in a car (local, remote or race NPC) is within 10 m in front of the doorway, and
  while anyone is inside the bay; checked every 0.2 s. No RPC: every peer derives it from
  positions it already has. A car at 40 km/h reaches the door as it finishes rolling up.
- **Door kind**: `DoorSpot.Kind` (set by `BuildingFootprint.ComputeDoors`) and
  `DoorIndex.Entry.Kind`/`.Bay`; `DoorIndex.Nearest(at, reach, BuildingKind.Garage, orInside: true)`
  also finds the garage whose bay holds the point (`GarageUi.GarageNear`: tuning works parked
  inside). On foot a garage is never a teleport interior: `NearestEntrance` skips it for the [E]
  prompt and `TryDoor`, and the server's `ServeDoor` refuses it. You walk in.
- Checks: `--garagehole` (cut self-check + survey). Offline drive-in (Riddes garage
  2582_1113_3): `--ride car,7,test_output/g.png --at 2583000.2,1113578.3 --heading 311.3
  --brake-at 2.3 --midshot 1.6` ends "inside garage ..., 0.00 m over its floor"; `--ride foot,...`
  walks in. Loopback (server 7821 `--admin-password pw56`): client `--garagecheck watch --at
  2582992.5,1113577.1`, then client `--garagecheck drive pw56 --at 2583000.2,1113578.3 --heading
  311.3 --drive-m 17`: the watcher logs the leaf rolling up and screenshots
  `test_output/garage_bay_entering_watch.png` / `garage_bay_inside_watch.png`.

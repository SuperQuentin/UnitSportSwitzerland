# Garage buildings and their roll-up doors (#56)

- **Data**: GWR GKLAS 1242 is `BuildingKind.Garage = 10` since `.bldg` v2 (same layout as v1,
  decoders take both; `NetworkChunkSource` deletes and refetches a cached `.bldg` older than v2).
  Riddes has 58. Rebuild: `--features-only --buildings <gpkg> --gwr <gwr data.sqlite>` — the GWR
  must be the canton's (`gwr_vs` for Riddes); the VD file matches 0 of 1,159 buildings and
  classifies everything Other. See `tools/gwr-classify-gklas-gkat`.
- **Facade** (`BuildingMeshBuilder`, `ps1_building.gdshader`): blue-grey walls, no window grid, a
  sign band over the door and a chequered band under the eave, both in `garage_neon` (magenta),
  unlit and never darkened, day or night. Flags ride in UV2.y: 1 = neon face, 2 = garage wall
  (then UV = metres along/up and UV2.x = eave height). Far off the chequer merges into one solid
  neon line, which is what reads across the valley.
- **The output is not sRGB-encoded**: a vertex colour goes through `SrgbToLinear` and is shown
  as that linear value, so a "dark grey" 0.26 renders almost black. Pick building and prop
  colours by what they render as (0.6 for a mid grey), not by their sRGB value.
- **Roll-up door** (`src/Vehicles/GarageDoors.cs`, client only, created next to `OccasionDecor`
  in `ClientWorld`): one `GarageDoor_<building index>` node per garage door, a child of the tile's
  `ChunkNode` (unloads with it). `Leaf` = slatted door hung from the lintel, rolled up by
  squashing its Y scale over 1 s; `Bay` = a few quads just in front of the wall behind it (tool
  wall, red roll cab, neon tube) — the building has no hole. Opens while any `FootPlayer` whose
  `Ride` is a car (local, remote or race NPC) is within 10 m in front of the doorway; checked
  every 0.2 s. No RPC: every peer derives it from positions it already has.
- **Door kind**: `DoorSpot.Kind` (set by `BuildingFootprint.ComputeDoors`) and
  `DoorIndex.Entry.Kind`; `DoorIndex.Nearest(at, reach, BuildingKind.Garage)` finds a garage door.
- Checks: `--ride car,2.2,test_output/garage_ride.png --at 2582988.73,1113598.81 --heading 311.3`
  drives at a garage in Riddes and shoots the open door. Loopback (server 7801 + A with
  `--raceauto --racestart 1500 --racenpc 2` + B on foot at the spawn): B saw GarageDoor_221 open
  for A's remote cars and close after them.

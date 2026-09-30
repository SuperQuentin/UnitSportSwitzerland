# Garage buildings and their roll-up doors (#56, #139)

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
- **A garage is an interior** (#139), entered through its door's portal like every other building
  (`terrain/door-portals`). The hollow bay of #56 (cut facade, a room in the building mesh,
  terrain carved under it) is gone: no facade cut, no terrain holes, the building mesh and
  collision are the plain solid again. The owner's call: "rendered like other interiors, with a
  portal door; hollow are not a great idea". `InteriorGenerator` plans it as one `RoomType.Garage`
  room whose entry is as wide and tall as the facade door (`BuildingFootprint.VehicleDoor`: garages
  and barns; `DoorHeightFor(kind, clear)` caps both at the hall's headroom so the openings match),
  with a lane from the door kept clear of furniture (`InteriorGenerator.Lane`: a garage's to 0.7 m
  short of its back wall, a barn's 9 m deep), so the prop car only fits in a double garage.
- **Roll-up door**: the facade bakes the shut slatted door in the leaf's own plane (6 cm out), which
  `ps1_building` drops at every height while the portal shows; the live one is
  `DoorLeaf.CreateRollUp`, on `DoorLink.Outside` like a barn's pair (`DoorLeaf.OnFacade`), hung from
  the lintel and squashed up into it by the shared door swing. Never solid: shut, the facade is.
  The step is flush (a car would hit a kerb). The old client-side `GarageDoors` node is gone.
- **Opening**: on foot, E like any door. Mounted, `InteriorManager.OpenForVehicle` asks the server
  to open a garage's or barn's door the vehicle heads at (outside within 10 m and 0.6 rad of
  square, aimed at the opening: `DoorIndex.VehicleDoorAhead`; inside, any of the building's
  doorways it heads at), by travel direction, so reversing in works; each door asked at most every
  4 s. The server allows vehicle doors from 16 m (`ServerVehicleDoorReach`). A car at 40 km/h from
  12 m out gets through: plan, interior build and 0.6 s swing are done in time.
- **Tuning inside**: `GarageUi.GarageNear` is true within 8 m of a garage door, or in a built garage
  interior (`InteriorManager.LayoutAt`).
- **Generated world**: side-street houses get a flat-roofed 3.4 x 6.2 m garage in the gap beside
  them half the time (position hash, not the village RNG; yielded last per tile, so no other
  building's index moved). ~20 around spawn.
- Checks (loopback, generated world, **`--traffic 0`**: a traffic car shoved the test car 7 m
  sideways off a barn's door): server `--server --port 7839 --generated-world --admin-password
  pw139`; client `--traffic 0 --garagecheck watch --at 2583265.1,1113302.6`, then client
  `--traffic 0 --garagecheck drive pw139 --at 2583262.8,1113298.6 --heading 80.1` (garage
  2583_1113_76). The driver drives in, stops, gets out (car parked inside), back in, reverses out:
  `RESULT: ok`; it traces its position against the door and what it hits. The watcher logs the
  leaf and screenshots `test_output/garage_bay_entering_watch.png` / `garage_bay_inside_watch.png`
  (the inside shot aims through the door's map). `--doorkind Agricultural` does a barn: watch `--at
  2582990.1,1113187.0`, drive `--at 2582987.5,1113183.2 --heading 345` (barn 2582_1113_4). With no
  `--at` the drive probe picks the nearest door and logs its LV95 and bearing, but online the
  teleport there was undone ~0.4 s later (both clients, cause not found), so pass `--at`.
  `--portaldemo` has a garage (F): open, rolling, shut, from inside.

# The Africa Twin at Riddes (easter egg, #55)

- **What**: `World/AfricaTwinEgg` puts a random Honda Africa Twin (every `MotorbikeCatalog` entry whose
  label says "Africa Twin", 28 of them) in a parking bay in front of house No. 15 near Riddes, WGS84
  46.166643, 7.214411 (LV95 ~2582680/1112822, tile 2582_1112), the creator's pick. Node name
  `veh_africatwin_egg` under `World/Vehicles`.
- **When**: `ChunkManager.TileEntered` (new: fires when a tile joins the rings) for that tile, if no
  unwrecked egg node exists. Ridden away = claimed = the node is gone (re-parking makes a new name), so
  the next time the tile enters the rings (after it unloaded) a new one, new model, new bay. One check
  per tile load; the slot search runs once per process on a worker.
- **Who**: the dedicated server (its coarse ring, 2 tiles, loads the tile when a player comes near),
  or a client with no peer at all (offline). A client connecting frees any local egg. Spawned by
  `VehicleManager.Place`: owner 0, so the **server is the authority**; `VehicleBody` on the dedicated
  server does not simulate it (no collision there) — it stands exactly where placed, asleep, 2 s sync
  heartbeat, until someone claims it through the usual `RequestClaim` flow.
- **The slot** (`FindSlots`, from the tile files through `ChunkManager.Source`, nothing kept): the
  building = the `.bldg` footprint under/nearest the point (plan triangles rasterised to 1 m). Parking
  cells of the cover raster within 40 m first, mid-bay (2.4 m+ off the aisle's asphalt). **TLM maps no
  car park there** (nothing in `tlm_areale_verkehrsareal` within 300 m), so the two rows east of the
  lane and the forecourt are hand-traced from the owner's aerial photo in
  `docs/data/cover_overrides.json` (#84, see `docs/notes/tools/land-cover.md`); the bike now stands in
  them. The fallback, kept for a cover file without them, guesses the same rows: two rows of
  bays on the far side of the access lane along the east facade, between lane and orchard. Bays every
  2.5 m along every drivable road within 25 m of the building (40 m of the point), on the side away from
  it, centre half a 5 m bay off the asphalt; free of buildings (2 m), trees (1.5 m), orchard/wood/water
  cover, level within 0.3 m. 29 bays there; heading perpendicular to the lane, nose in or backed in.
- **Data**: the 3x3 km around the point (2581..2583 / 1111..1113) was built for this: swissALTI3D 2024,
  swissBUILDINGS3D 3.0 sheets 1305-42/-44 (`buildings_riddes.gpkg`, GDAL from micromamba — none on the
  WSL box), GWR VS in `ressources/data/gwr_vs/`. swissBUILDINGS3D 2020 has the complex **under
  construction**: houses 15/19/21 are one merged "Im Bau" hull, so the model does not show three houses.
- **Check**: `<godot> --path . -- --connect 127.0.0.1:P --eggcheck ride|watch --at 2582700,1112830`
  (`World/EggProbe`): logs the egg and the other players each second; `ride` gets on, rides 40 m, both
  go 70 km to the Mollendruz (tile unloads on the server), the rider parks there, both come back.
  Measured: both clients saw the same model at the same spot (auth 1), B saw it vanish and A on it at
  the Mollendruz, a different model stood there after the return. Top-down shot:
  `--at 2582680,1112821 --shot 34186,490,27675,-89.9,0,25,out.png` (origin 2548500/1140500).

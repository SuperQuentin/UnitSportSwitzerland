# Vehicles (`src/Vehicles/`)

Machines left in the world, damage, wrecks and their network sync.

Index only: one line per note in `docs/notes/vehicles/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/vehicles`.

## Architecture

- `garage-tuning` — Garage tuning (#56): `CarTuning` 15 slots in a long, tyre models, `GarageUi` (T, `GarageNear` hook, `--tuning`), hinged car doors (G), their sync, `--tuningcheck`, `--garagecheck`
- `vehicles-vs-equipment` — Vehicles vs equipment: (`src/Vehicles/`). `Rideable.IsVehicle` (bike, helicopter, plane) splits machines that are...
- `garage-buildings` — Garages (#56): GKLAS 1242 -> `BuildingKind.Garage` (.bldg v2), sign and eave stripe flags in UV2.y, `GarageDoors` roll-up doors that open for any car in front, `DoorIndex.Nearest(at, reach, kind)`...
- trucks and buses park as whole trains (trailer, angles, bus flags in `VehicleState`), a lone trailer is `RideKind.Trailer` (`ParkedTrailer`): see the player note `trucks-buses`
- `admin-only-spawning` — Only an admin conjures vehicles online: the server counts claims and refuses other parks unless `MayPark`...

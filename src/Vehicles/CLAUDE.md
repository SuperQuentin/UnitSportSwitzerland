# Vehicles (`src/Vehicles/`)

Machines left in the world, damage, wrecks and their network sync.

Index only: one line per note in `docs/notes/vehicles/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/vehicles`.

## Architecture

- `vehicles-vs-equipment` — Vehicles vs equipment: (`src/Vehicles/`). `Rideable.IsVehicle` (bike, helicopter, plane) splits machines that are...
- `garage-tuning` — Garages (#56): GKLAS 1242 -> `BuildingKind.Garage` (.bldg v2), neon facade flags in UV2.y, `GarageDoors` roll-up doors that open for any car in front, `DoorIndex.Nearest(at, reach, kind)`...

# Vehicles (`src/Vehicles/`)

Machines left in the world, damage, wrecks and their network sync.

Index only: one line per note in `docs/notes/vehicles/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/vehicles`.

## Architecture

- `garage-tuning` — Garage tuning (#56): `CarTuning` 15 slots in a long, tyre models, `GarageUi` (T, `GarageNear` hook, `--tuning`), hinged car doors (G), their sync, `--tuningcheck`, `--garagecheck`
- `vehicles-vs-equipment` — Vehicles vs equipment: (`src/Vehicles/`). `Rideable.IsVehicle` (bike, helicopter, plane) splits machines that are...

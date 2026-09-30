# Vehicles (`src/Vehicles/`)

Machines left in the world, damage, wrecks and their network sync.

Index only: one line per note in `docs/notes/vehicles/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/vehicles`.

## Architecture

- `vehicles-vs-equipment` — Vehicles vs equipment: (`src/Vehicles/`). `Rideable.IsVehicle` (bike, helicopter, plane) splits machines that are...

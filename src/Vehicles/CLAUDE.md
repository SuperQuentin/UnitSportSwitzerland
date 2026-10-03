# Vehicles (`src/Vehicles/`)

Machines left in the world, damage, wrecks and their network sync.

Index only: one line per note in `docs/notes/vehicles/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/vehicles`.

## Architecture

- `garage-tuning` — Garage tuning (#56): `CarTuning` 15 slots in a long, tyre models, `GarageUi` (T, `GarageNear` hook, `--tuning`), hinged car doors (G), their sync, `--tuningcheck`, `--garagecheck`
- `vehicles-vs-equipment` — Vehicles vs equipment: (`src/Vehicles/`). `Rideable.IsVehicle` (bike, helicopter, plane) splits machines that are...
- `garage-buildings` — Garages (#56, #139): GKLAS 1242 -> `BuildingKind.Garage` (.bldg v2), sign and eave stripe flags in UV2.y, a portal interior (no hollow bay), roll-up `DoorLeaf` on the facade, doors open for a vehicle heading at them, generated-world garages, `--garagecheck drive/watch` (`--traffic 0`)
- passengers (#158): `PassengerService` (World/Passengers) seats players in vehicles others drive and moves a vehicle between hosts; see the player note `passengers`
- trucks and buses park as whole trains (trailer, angles, bus flags in `VehicleState`), a lone trailer is `RideKind.Trailer` (`ParkedTrailer`): see the player note `trucks-buses`
- `door-reach` — Getting in by the door (#261): `VehicleReach` aims at one door (view ray, then the door you stand at, 1.35 m), E opens a shut door then gets in, G works it, door-less machines by hull/entry point, outline on the door, `TryGetIn` for probes
- `admin-only-spawning` — Only an admin conjures vehicles online: the server counts claims and refuses other parks unless `MayPark`...
- `perf-parked-vehicles` — Nothing per frame for a parked vehicle: dressed once at rest, cached sections, one reused ground ray query (#221)
- `vehicles-in-holds` — Vehicles carried in a carrier's hold (#418): a carrier fills `VehicleDeck.CargoBays` (ramp/tailgate = door parts), fit from hull bounds (`CargoFit`), carried like a deck walker (`FootPlayer.Hold`), handbrake ties down, parked in a hold = `VehicleState.Carrier` posed from the carrier on every peer, `--holdcheck`, `tools/holdnetcheck.sh`
- `airstairs` — Mobile airstairs (#417): RideKind 126, platform raised to a door's sill found from the aircraft deck's `DoorStep` boxes (any walkable `Flyer`), docks when let go by a door, parked height follows the sill, shoved clear by a taxiing aircraft, `AirstairsDock.PlaceAt` for stands (#422), `--stairscheck`
- `ground-query` — the ground under a point for a vehicle (driven truck sections, parked `VehicleBody`) is `World.GroundQuery.Under(self, ray, exclude, p, terrain, pastPlayers)`; never another copy (#221)

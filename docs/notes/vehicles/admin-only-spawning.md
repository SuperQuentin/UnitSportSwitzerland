# Only an admin conjures vehicles online

- **Only an admin conjures vehicles online** (issue #32): the server counts the vehicles each peer
  has claimed and not yet parked (`VehicleManager._driving`); a park beyond that is a vehicle
  spawned from the travel menu and is allowed only if `MayPark` says so — wired in `ServerWorld` to
  "admin, or the car a race issued you" (`RaceManager.IssuedCars`, one per entrant, consumed on
  park). A refusal is `ParkRefused` -> `VehicleManager.Refused` -> a toast; the vehicle is simply
  not left. Verified on loopback with `--econcheck` (the items `commands` note).

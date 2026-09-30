# Only an admin conjures vehicles online

- **Only an admin conjures vehicles online** (issue #32): the server counts the vehicles each peer
  has claimed and not yet parked (`VehicleManager._driving`); a park beyond that is a vehicle
  spawned from the travel menu and is allowed only if `MayPark` says so — wired in `ServerWorld` to
  "a wreck, an admin, or a vehicle a race put you on". A race with a vehicle mount credits one park
  per entrant to whoever **simulates** it (`RaceManager.TakeIssued`, keyed by `SimOf`): race NPCs
  are run by an ordinary client, and their wrecks and throw-offs park through that client. Wrecks
  always pass: they cannot be driven and burn out in 90 s. A refusal is `ParkRefused` -> `VehicleManager.Refused` -> a toast; the vehicle is simply
  not left. Verified on loopback with `--econcheck` (the items `commands` note).

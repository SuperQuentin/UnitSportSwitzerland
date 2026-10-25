# /despawn: parked vehicles and dropped items out of the world (#1200)

- **Command**: `/despawn <vehicles | items | all> [radius m | everywhere]`, 30 m by default, up to
  5000 m (`AdminArgs.TryDespawn`, unit-tested). Admin online, anyone offline. Online the server
  measures from the sender's body; the server console (no body) may only say `everywhere`. Offline
  it measures from the camera.
- **Debug menu** (F9), section *Despawn*: *Vehicles near*, *Items near*, *Everything everywhere*. The
  buttons only send the command through `ItemController.RunCommand`, so the server checks the admin
  and the answer shows in the chat.
- **How**: `VehicleManager.Despawn(at, radius)` and `DroppedItems.Despawn(at, radius)`, server or
  offline only. Each goes the way a claim or a pick-up does: `Removing` first (the object containers
  drop it from their book, #689), then `QueueFree`, and the `MultiplayerSpawner` removes it on every
  client. A vehicle or item with a claim or pick-up in flight is skipped, and so is a client's
  stand-in (`Proxy`). Driven vehicles are not in the manager, so they are never touched.
- **What it does not cover**: dormant and hall vehicles that were never woken (they are not nodes; a
  despawned woken one sleeps again in its slot), traffic, bots (`/bot clear`), built pieces. A carrier
  removed without its cargo leaves the cargo where the hold was.
- **Bots**: an admin command; a bot needs nothing from it.
- **Checks**: `--chatcheck` (offline: parsing, the radius, everywhere, completion);
  `tools/econcheck.sh` (net, fixture: an admin's `/despawn all` takes their bike and a dropped stone
  out of their own world through the server).

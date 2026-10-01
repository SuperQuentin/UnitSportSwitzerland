# Loading screen

- **What it shows** (`Ui/LoadingScreen`, CanvasLayer 50): the real stage in big type, a bar that only
  moves forward (eased), a detail line, Cancel (also Esc / B), and under it a rotating line from
  `LoadingPhrases` ("Calibrating cowbells...") every 2.6 s. The jokes never replace the real status.
- **Where the numbers come from**: `ClientWorld.Stage / LoadFraction / LoadDetail`, polled by the
  shell each frame (`ClientWorld.TrackLoading`). Stages: ReadingMap (manifest) -> BuildingWorld ->
  online only: Connecting (15 s give-up; ENet's own is ~30 s) -> SyncingTerrain
  (`ClientTerrainSync.IndexFinished`: the tile index only; places and horizon arrive in play) -> WaitingForPlayer (our node spawned
  and on foot) -> PlacingYou (the `SpawnPoint` still alive) -> BuildingTerrain
  (`ChunkManager.PlayableNear(eye, 0)`: only the tile you stand on, drawn at any detail, plus
  its collision when a body (not the fly camera) is on it; every other tile, refinement, roads and buildings stream in after; goes ahead after 15 s anyway)
  -> Ready. Hosting adds "Starting your server" before all of it.
- **Frames to draw**: `ClientWorld._Ready` yields a frame (`Breathe`) at a few points when launched
  from the title, with an `IsInsideTree()` check after each await (Cancel may free the world
  mid-build). Command-line runs do not yield, so the probes see the old build order.
- **Failure** (connection refused, timeout, kicked during the load) sets `Stage = Failed`; the shell
  leaves the world and shows the reason on the Multiplayer screen's banner (a dialog for solo).

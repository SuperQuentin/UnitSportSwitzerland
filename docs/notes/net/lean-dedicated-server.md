# The dedicated server stays lean

- A client's player on the server is a **data proxy** (`FootPlayer.NetProxy`, `NetworkManager.
  DedicatedServer` — not `Multiplayer.IsServer()`, which is also true offline): no visual, no held
  item, no processing, collision disabled; the replication setter writes `Position` directly.
- `Engine.MaxFps = 60` (headless had no cap).
- Terrain: the server ring is 5x5 tiles of **coarse** `.terrc` (5 KB) and loads neither holes nor
  the cover raster: nothing on the server builds on the ground. It was ~50 MB per spread player.
- `ChunkStreamer`: file read + CRC + deflate on a worker; `DeliverPrepared` sends the RPCs from
  `_Process` (RPCs off the main thread never arrive); one reused fragment buffer.
- Parked vehicles: 20 Hz while moving, every 2 s asleep, flags `OnChange`.

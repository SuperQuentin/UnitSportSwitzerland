# The dedicated server stays lean

- A client's player on the server is a **data proxy** (`FootPlayer.NetProxy`, `NetworkManager.
  DedicatedServer` — not `Multiplayer.IsServer()`, which is also true offline): no visual, no held
  item, no processing, collision disabled; the replication setter writes `Position` directly.
- `Engine.MaxFps = 60` (headless had no cap).
- Terrain: the server ring is 5x5 tiles of **coarse** `.terrc` (5 KB) and loads neither holes nor
  the cover raster: nothing on the server builds on the ground. It was ~50 MB per spread player.
  **Stride 0 is not "full"** (issue #65): with meshes off every want has stride 0, and
  `ChunkManager.StartBuild`'s `needsFullGrid` read `stride < CoarseStride` as "needs the 2 MB grid",
  so the server read full grids all along (`LoadStats` said 236 full / 0 coarse). Unnoticed while
  only the 91 real tiles could load; with the generated fill (#27) every tile loads, and each held
  one cost ~2 MB of grid plus generation garbage. Now `needsFullGrid` counts the stride only when
  `BuildMeshes`, so real and generated tiles are both served coarse (`FallbackChunkSource.
  LoadCoarseChunkAsync`: stride-10 generation, blend from coarse neighbours; roads, buildings,
  trees and cover are never generated for the rings). Interiors still read a tile's buildings,
  roads and full grid lazily, once per building entered. Server `CachingChunkSource`: 32 MB.
  32 players, 60 s, 142 → 191 tiles: heap 447 → 639 MB and WS 661 → 866 MB before; after, WS
  259 → 269 MB, heap 41–55 MB, flat. Soak 32 × 10 min: WS 279 → 282 MB over the last 5 min
  (~205 tiles held), heap ~50 MB, 0 UDP drops, 0 dropped bots, observer 0 freezes. Height from the coarse grid on generated ground agreed with
  the client's standing height within 0.11 m (steep ground can differ more: 10 m bilinear).
- `ChunkStreamer`: file read + CRC + deflate on a worker; `DeliverPrepared` sends the RPCs from
  `_Process` (RPCs off the main thread never arrive); one reused fragment buffer.
- Parked vehicles: 20 Hz while moving, every 2 s asleep, flags `OnChange`.

# "Not connected to a server" is NOT a transient failure for a game that has no server

- **"Not connected to a server" is NOT a transient failure for a game that has no server.**
  This one cost ten minutes on every cold start and hid for months behind plausible explanations.
  `NetworkChunkSource` falls through to the network whenever a local file is absent — and a
  `.holes` file is absent for **6,067 of 6,699 tiles**, because almost nothing has a tunnel. With
  no server, `ChunkStreamer` reported "not connected" as *transient*, so `FetchLoopAsync` retried
  five times with backoff — 0.4 + 0.9 + 2 + 4 = **7.3 s per tile** — while holding one of the six
  global fetch slots. Six slots over 7.3 s is a hard ceiling of **0.8 tiles per second** whatever
  the disk does. Measured before the fix: 887 s of worker time, **100%** of it in
  `holes+cover`, with `.terr` reads at 0%. After: 0.0 s, and a cold start of **0.6 s** where the
  budget had been 600. The fix is `ChunkStreamer.ServerReachable` — a per-frame snapshot, because
  connectivity is only knowable on the main thread — and `ObtainAsync` returning null immediately
  when there is no peer at all. Deliberately *no peer* rather than *not currently connected*: a
  client mid-join has a peer whose status is `Connecting`, and a null reaching `ChunkManager` is
  recorded as "this tile has no roads" for the rest of the session.
  **The lesson generalises**: profile the load path before optimising it. Tile size, mesh cost and
  LOD radius are the obvious suspects and were together under 5% of the time.

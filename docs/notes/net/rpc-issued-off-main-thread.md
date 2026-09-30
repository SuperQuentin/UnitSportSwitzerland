# An RPC issued off the main thread does not throw — it never arrives

- **An RPC issued off the main thread does not throw — it never arrives.** `ChunkManager` loads
  and meshes tiles on the thread pool, so `ChunkStreamer.FetchAsync` runs there; the request
  and the connectivity check are both `CallDeferred` onto the main thread. The symptom of
  getting this wrong is silence: the tile simply stays blank forever.

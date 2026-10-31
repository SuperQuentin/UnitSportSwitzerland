# Terrain streaming

- **Terrain streaming** (`Net/ChunkStreamer`, `Terrain/NetworkChunkSource`): the server serves
  generated files to clients that lack them. `IChunkSource` was already the seam, so
  `NetworkChunkSource` decorates `LocalChunkSource` with three tiers — shipped -> cache
  (`user://chunk_cache/`, overridable with `--cache`) -> server. The transfer unit is the
  **raw file**, cached under its ordinary filename, so the ordinary decoders read a streamed
  tile exactly like a shipped one and the server does no decoding. Files are sliced into 24 KB
  fragments on transfer channel 2 (bulk data on the default channel head-of-line blocks every
  position update behind it), deflated when that helps, and CRC-checked before being cached.
  `ClientTerrainSync` fetches the server manifest on join and merges its tile list into
  `ChunkManager._available` — without that merge the LOD rings skip unknown tiles and nothing
  is ever requested. It also saves that index to the cache, so tiles streamed in an earlier
  session are reachable offline.
- **A returning player** reuses the last visit (#1380): the index cached per server and merged at boot, a server digest instead of re-downloads, ready without the horizon: `net/rejoin-cache`.
- **HTTP first** when the server names a mirror (`--tiles-url`, #651): the bulk files come from a
  static HTTP server and ENet is the fallback (`net/http-tiles`).
- **Cache writes use a unique temp name** (`NetworkChunkSource.WriteCache`, issue #65): two
  fetches of the same asset can complete together (e.g. a blend and the rings), and a shared
  `<file>.part` was moved away by one under the other ("cache write failed ... .terrc.part", 19 in
  a 50 s cold stream). Now `<file>.<guid>.part`, moved over the target (same bytes, last wins),
  deleted if the write fails; eviction skips `.part` files still being written.
- **A lost answer is not the end of the session (#1032).** The mirror address (`HttpBase`) and the tile
  index (`Manifest`) are fetched by `ClientTerrainSync.FetchPersistentAsync`: a timeout, "busy" or "not
  connected yet" is asked again with backoff (2, 4, 8, 15, 15 s, six attempts); only "not on this server"
  is final. Before, one timed-out manifest left a phone (no local tiles) in mid-air for the whole session.
  `ChunkStreamer` also starts a request's clock when the deferred RPC actually goes out, and stops it
  while the client's frames stall (a gap of over 1 s between two `ExpireClientRequests` moves every
  pending start by the gap): replies already queued behind a stall are handled first. Seen on Android
  joining a server: 18 s and 65 s without a frame while joining (first-use JIT, slow frames), the 30 s
  timeout expiring answers the server had sent (instrumented: received and answered at once).
- **The root cause on phones was a build cap of 0 (#1032).** `ChunkManager.PlayMaxConcurrentBuilds`
  (the streaming cap) read `MobileMaxConcurrentBuilds` while it was still 0: C# runs static field
  initializers in the order they are written, and the mobile cap was declared below. With the build
  setting on auto (`MaxConcurrentBuilds = 0`, the phone default) a phone joining any server had no build
  slot: no tile was built, so none was fetched, and the player stood on nothing below the coarse terrain.
  Solo used `OfflineMaxConcurrentBuilds`, declared after it, and worked; a desktop with a saved explicit
  `builds=N` never saw it (the override wins). Reproduce on desktop with `--mobile --builds 0`. Now the
  mobile cap comes first and `MaxConcurrentBuilds` is never below 1. **Rule:** a static readonly field
  that reads another one must be declared after it, or be a property.

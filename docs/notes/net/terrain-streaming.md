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
- **Cache writes use a unique temp name** (`NetworkChunkSource.WriteCache`, issue #65): two
  fetches of the same asset can complete together (e.g. a blend and the rings), and a shared
  `<file>.part` was moved away by one under the other ("cache write failed ... .terrc.part", 19 in
  a 50 s cold stream). Now `<file>.<guid>.part`, moved over the target (same bytes, last wins),
  deleted if the write fails; eviction skips `.part` files still being written.

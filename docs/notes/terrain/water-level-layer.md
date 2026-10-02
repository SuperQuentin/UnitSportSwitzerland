# Water level layer: the in-memory still water of a tile (#299, for #298)

- **Shape.** A source hands over `Terrain.Format.WaterTile` (`tools/TerrainFormat/WaterTile.cs`):
  `float[] Level` of 501 x 501 samples every 2 m (`WaterTile.Stride`, `Size`), row 0 the north
  edge, column 0 the west edge, exactly `ChunkGrid`'s layout at stride 2: sample (c, r) is LV95
  E = MinE + 2c, N = MaxN - 2r and height-grid vertex (2c, 2r). `NaN` = dry. Optional
  `float[]? FetchM` (same layout): metres of open water the wind blows over to reach the sample.
  Edge samples are shared with the neighbour tile and must agree with it.
- **Source hook.** `IChunkSource.LoadWaterAsync(TileId, ct)`, a default interface method returning
  null. `CachingChunkSource` (not cached yet), `FallbackChunkSource` (null on generated tiles) and
  `NetworkChunkSource` (local only, nothing streamed yet) forward it. `FixtureChunkSource`
  answers it for the lake course. **#298**: decode the `.water` file into a `WaterTile` in
  `LocalChunkSource.LoadWaterAsync`, give it a cache slot, stream it in `NetworkChunkSource`,
  and fill `FetchM` from the whole lake (the runtime fallback only sees one tile).
- **Runtime.** The tile worker turns it into `Terrain.WaterLayer` (`WaterLayer.Create(tile, grid)`):
  the same `Level` plus a `byte[] Scale` (0..255 = 0..1), the share of the sea state each sample
  gets = `FetchFactor(fetch)` (1 - e^(-fetch/1500 m)) x `DepthFactor(level - bed)` (smoothstep to
  full at 6 m). The bed is the tile's height grid: with #298's bathymetry in it, depth is real.
  Kept on `ChunkState.Water` where the cover is kept (tiles finer than the coarse stride) and on a
  server (every tile it holds), dropped on far tiles.
- **Legacy fallback.** No source layer: `WaterLayer.FromCover(grid, cover)`, water where the cover
  raster says `Water`, level = terrain + 0.12 m (what `WaterMeshBuilder` always drew). Needs the
  full grid and the cover, so a server has no legacy water. Depth 0.12 m: no waves (depth factor
  ~0), no sinking (under every wading depth), cars still drive on legacy lakes. Fetch = twice the
  distance to the nearest dry sample in the tile (chamfer, tile edges open, cap 4 km).
- **Queries.** `ChunkManager.TryGetWaterLevel(Vector3 world, out float stillLevel)` and
  `TryGetWater(world, out stillLevel, out waveScale)`: bilinear over the wet corners of the
  sample square, false when the nearest sample is dry or the tile's water is not loaded.
  Waves on top: `World.WaterField` (`docs/notes/world/water-field.md`).
- **Memory.** 1.25 MB per tile that has water (float level + byte scale); tiles with no wet sample
  hold nothing. A sparse encoding is #298's choice on disk; decode it into the dense arrays.

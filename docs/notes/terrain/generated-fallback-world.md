# Generated fallback world

- **Generated fallback world** (`Terrain/ProceduralWorld`, `Terrain/FallbackChunkSource`): a client
  with no tiles at all (a fresh clone) gets a stand-in instead of a void — an alpine valley through
  the spawn point with a river on a flat bed (water from the cover raster, like the real one), a
  road and a railway along the floor, villages with side streets and a church every ~2.6 km, farms
  and alpine huts, forest to a wandering tree line, rock, scree, glacier, vineyards on the sunny
  side, orchards, a 100 km horizon. 81 x 81 tiles, all in the **ordinary formats**, served through
  the ordinary `IChunkSource` seam under `CachingChunkSource`, so roads, traffic, trains, doors,
  interiors, collision and gathering all work on it unchanged. Everything is a pure function of
  LV95 position, so seams are bit-identical and the stride-10 grid equals the decimated full one
  (both checked). Noise is sampled on a **world-anchored 5 m lattice** and interpolated: evaluated
  per vertex it cost 320 ms a tile; now ~30-40 ms, cover ~10 ms (classified from 10 m samples, since
  every tile in the rings asks for cover), horizon 0.2 s. The origin goes on the spawn point.
  **Real tiles replace it**: `ChunkManager.MergeAvailableTiles` calls `RetireFallback` first, which
  unloads every generated tile, switches the source off, flushes the cache (`CachingChunkSource.Clear`
  bumps an epoch so a fetch straddling it is not cached), clears the horizon, and raises
  `TerrainReplaced` for the systems that keep their own tile caches (`Surfaces`, `Ambience`,
  `Gathering`, `Traffic`). Joining a server retires it **before** adopting the server's origin
  (`ClientTerrainSync.Adopt`, run on the main thread). Test it with `--chunks <empty dir> --cache
  <empty dir>`; a server still refuses to start without real terrain.

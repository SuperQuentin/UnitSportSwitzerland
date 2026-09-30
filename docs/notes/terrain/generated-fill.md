# Generated fill: generated and real tiles side by side

- **Generated fill** (`Terrain/ProceduralWorld`, `ProceduralWorld.Blend`, `Terrain/FallbackChunkSource`,
  issue #27): every tile with no real data is generated, and generated tiles sit **beside** real
  ones with no visible seam — round a MapSetup zone, a server streaming part of the country, a
  download with holes, and everywhere on a fresh clone. The generator: an alpine valley running
  east-west through its **anchor** with a river on a flat bed (water from the cover raster, like the
  real one), a road and a railway along the floor, villages with side streets and a church every
  ~2.6 km, farms and alpine huts, forest to a wandering tree line, rock, scree, glacier, vineyards on
  the sunny side, orchards; high massif far from the valley. All in the **ordinary formats**, served
  through the ordinary `IChunkSource` seam under `CachingChunkSource`, so roads, traffic, trains,
  doors, interiors, collision and gathering all work on it unchanged. Everything is a pure function
  of LV95 position, so seams are bit-identical and the stride-10 grid equals the decimated full one.
  Noise is sampled on a **world-anchored 5 m lattice** and interpolated: evaluated per vertex it cost
  320 ms a tile; now ~30-40 ms, cover ~10 ms (classified from 10 m samples).
  **Ownership**: a tile is real if it is in `ChunkManager._available` (manifest + anything merged),
  generated if not real and inside the **fill domain** — the spawn tile's box and the real set's
  bounding box, each grown by 40 tiles (`FallbackChunkSource.FillRadiusTiles`); it only grows.
  `FallbackChunkSource` holds an immutable `Snapshot` (real set, domain, version) swapped by `SetReal`
  and read lock-free; the rings ask `IsAvailable = _available || Covers`. `_available`,
  `AvailableTiles` and `AvailableTileCount` stay **real only** — corridor surveys must never request
  a generated tile over the network, and "does this client have a world of its own" means real.
  **Anchor**: the generator is centred on `SpawnPoint.DefaultLv95E/N` (Riddes) on every peer and the
  server, not on this run's spawn, so everyone generates the same world. The origin with no local
  terrain is still this run's spawn point.
  **The blend** (`ProceduralWorld.Blend`): `h = (1 − W)·G + W·R + D`, a convex mix, so blended ground
  always lies between the generated and the real (see the trench gotcha). R is the real low-pass
  carried outward: each real tile within 3 km (`Band`) contributes its **100 m knots** (the
  `horizon.bin` lattice) at its nearest point, weighted by inverse distance — continuous where
  nearest-edge extrusion jumps on the medial axis of a hole or a notch. Carried d metres, the knots
  are read from a **pyramid** (`RealTile.Levels`: knots, then tent area-averages on 200 m / 500 m /
  1 km lattices, then the mean) at a spacing of ~d/2, blended across levels by a smoothstep in
  log-distance: the nearest point is constant along every line across the edge, so at one spacing
  real relief was extruded as 3 km streaks. Every lattice is interpolated **Catmull-Rom** (C1, exact on
  a node), not bilinear, whose slope break at each node became a 100 m crease carried across the
  band. W = 1 − smoothstep(reach / 3 km), reach a **soft minimum** (150 m) of the distances with each
  tile's term faded by its own distance — a hard minimum creased on the medial axis (an X across a
  one-tile hole), an unfaded soft one jumped when a tile left the band. D, within 40 m
  (`DetailBand`, which must stay under 100 m or horizon = grid breaks), continues the real surface
  to first order from the edge neighbours' grids: residual `R − L0` plus the real slope across the
  edge, the slope over 1 m on a full grid (10 m at a real corner, the only point two tiles share)
  and faded out within the first 10 m (`SlopeFade`), so it is zero at every 10 m point. That makes the
  seam **C1** (BlendCheck: kink 0.012 m against the ground's own 0.053 m) while a coarse tile stays
  exactly the decimation of the full one. W and W·R live on a world-anchored **10 m lattice**, so
  coarse tiles and the horizon read stored values; a generated vertex on a real edge **copies** the
  real quantised height (bit-identical seam; the mix alone is within 11 cm, the 10 m lattice's
  interpolation of a cubic). Rivers are not drawn where the blend tilts the bed past 1.5%.
  `FallbackChunkSource.BlendFor(tile, full)` loads the real neighbours through the cache above it
  (`Neighbours`), shared per tile and LRU 24; full grids only for the four edge neighbours of a
  full-resolution tile, coarse otherwise; knots from `horizon.bin` when loaded, else extracted from
  the coarse grid — the same bits. Cover uses the coarse blend (identical at the 10 m points it
  reads), so far tiles never read a real full grid.
  **Merging real tiles** (`ChunkManager.MergeAvailableTiles`): `SetReal`, then the new ids and every
  generated tile within 4 of them are **unloaded** (not rebuilt in place: a commit whose roads,
  buildings or trees are null leaves the old ones standing), dropped from the cache
  (`CachingChunkSource.Invalidate(predicate)`, which bumps the epoch so a straddling fetch is not
  cached), the horizon reloads, and `TerrainReplaced(affected)` fires — `ClientWorld` re-places the
  player only if their own tile is affected. 87 tiles merge in 6 ms. `ResetAll(moveOrigin)` throws
  everything away for a rebase (`ClientTerrainSync.Adopt`, when the client has no real tiles).
  **Horizon**: `FallbackChunkSource.LoadHorizonAsync` merges the real index with generated samples for
  the domain + 60 tiles (knots-only blend near real ground; unblended samples cached across reloads):
  45k tiles in ~0.2 s. `HorizonLayer.Reload` queues a re-run asked for mid-load and keeps old blocks
  drawn until their replacements commit, since every merge reloads it.
  **Server**: `ServerWorld` runs the same source (and a cache), so its grid-only `ChunkManager`, the
  interiors and loot see generated ground and houses; its status line prints the ground under each
  player and whether it is generated. `--generated-world` starts a server with no terrain at all
  (origin at the anchor, served as `ChunkStreamer.ManifestOverride`); without it an empty server
  still refuses to start. **Off switch**: Settings → World → Generated terrain
  (`GameSettings.GeneratedFill`, live via `ChunkManager.SetFallbackEnabled`), `--generated off` for
  one run (also on a server). A faint "generated terrain" note (`Core/GeneratedTerrainNote`) shows
  while the camera is over generated ground — a note, not a tint, since the blend exists so the
  border cannot be seen. Cost: a blended full tile ~46 ms against ~24 ms plain. `--fly` across a
  border at 150 m/s (992 tiles, 16 workers): 4 of 7 runs had no frame over 33 ms, the others one
  each (33, 50, 62 ms), which the perf log files as "other" — no slow commit, GC or GPU frame
  behind them; over purely generated ground, 0 in 2 runs. Suspected: blend maths on every core
  starving the main thread. **Open.** Check: `dotnet run --project tools/BlendCheck -c
  Release` (see Commands).

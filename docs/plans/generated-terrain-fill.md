# Generated terrain fill: generated and real tiles side by side

Status: **plan, not started**. Builds on `84f5ebf` (generated fallback world, all-or-nothing).

## Goal

Every tile the world has no real data for is generated, and generated tiles sit **next to** real
ones with no visible seam. Today the fallback is all-or-nothing: the whole generated world is
retired the moment the first real tile arrives, so a partial region (a MapSetup zone, a server
that streams part of the country, a download with holes) is surrounded by void again.

## Decisions (agreed)

| Question | Decision |
|---|---|
| Fill beyond a real region even when local terrain exists? | **Always fill.** `--generated off` disables it for one run. |
| Width of the blend band | **~3 km**: a 1,500 m mismatch resolves at up to ~37°. |
| Multiplayer | **Same on every peer, and generated on the server too**: the generator is anchored to a fixed point, and the server runs the same source so interiors, loot and height queries work on generated ground. |
| Roads, rivers, railway at the border | **Dead ends are acceptable** for now. Joining real road ends to the generated network is a possible later phase. |

## What exists (from `84f5ebf`)

- `ProceduralWorld` (+ `.Settlements`): a pure generator. Valley, river, road, railway, villages,
  farms, cover, trees, horizon. Noise sits on a world-anchored 5 m lattice; seams are
  bit-identical and coarse tiles equal the decimated full tile (both checked).
- `FallbackChunkSource`: serves generated tiles for ids the generator owns, while `Active`.
- `ChunkManager.UseFallback` / `RetireFallback` / `TerrainReplaced`, and
  `ClientTerrainSync.Adopt`, which retires the fallback before rebasing the origin.
- Cache flushing: `CachingChunkSource.Clear` (with an epoch), `HorizonLayer.Clear` (with an
  epoch), and `Forget()` on `Surfaces`, `Ambience`, `Gathering` and `Traffic`.
- The quit-crash fix in `ChunkManager._ExitTree` and the thread marshalling in
  `ClientTerrainSync`. Both stay as they are.

## Design

### 1. Tile ownership

A tile is **real** if it is in `ChunkManager._available`: the local manifest plus anything merged
(the server index, the cached index). It is **generated** if it is not real and lies inside the
**fill domain**:

- the box around the player's spawn tile ± `FillRadiusTiles` (40), and
- the bounding box of the real set, expanded by `FillRadiusTiles`.

The domain only ever grows, and is recomputed when the real set changes. `_available` stays
real-only. The ring evaluator asks `IsAvailable(id) = _available.Contains(id) ||
fallback.Covers(id)`. Callers that pass `AvailableTiles` to corridor surveys keep getting real
tiles only, which is correct: they must never request a missing asset over the network.

`FallbackChunkSource` holds an immutable **snapshot**: the real set, the domain, and a
`Version`. It is swapped atomically by `SetReal`, and read lock-free by workers and the ring
evaluator.

### 2. Anchor

`ProceduralWorld` is anchored to `SpawnPoint.DefaultLv95E/N` (Riddes), no longer to the actual
spawn point, so every client and the server generate the same world. The valley runs east-west
through the anchor; far from it the generator yields high massif. `--at` elsewhere lands wherever
that point is. The origin with no local terrain stays at the spawn point, as today.

### 3. The height blend

For a generated tile T with real tiles nearby:

```
h(p) = G(p) + S(p) + D(p)
```

- **G**: the generated height (`Raw`, as today).
- **S, the smooth correction.** Over every real tile k within the band:
  - `q_k(p)` = p clamped onto k's square (always on k's boundary for p outside k);
    `dist_k` = |p − q_k|.
  - `sm_k = Rs_k(q) − Gs_k(q)`. `Rs` is the bilinear of k's **11×11 real samples at 100 m**
    (the horizon lattice); `Gs` is the bilinear of G at the same 121 points.
  - `w_k = (1 − smoothstep(0, Band, dist_k)) / (dist_k² + 1)`
  - `S = (1 − smoothstep(0, Band, min dist)) · Σ w_k·sm_k / Σ w_k`, and 0 when no k is within
    the band.

  This is inverse-distance weighting, not nearest-edge extrusion. Nearest-edge extrusion jumps
  on the medial axis (a one-tile hole in a real region, a notch), which would be a cliff; IDW is
  continuous everywhere. Low-passing through the 100 m lattice stops real fine detail being
  extruded 3 km outward as streaks.
- **D, the detail correction.** Only real tiles within `DetailBand` (150 m), which means the 8
  neighbours:
  - `det_k = (R_k(q) − Rs_k(q)) − (G(q) − Gs_k(q))`, where `R` is the real mesh height
    (`SampleMeshHeight`).
  - `w'_k = fd_k / (dist_k² + 1)`, with `fd = 1 − smoothstep(0, DetailBand, dist)`.
  - `D = Σ w'_k·fd_k·det_k / Σ w'_k`
  - This carries the real terrain's roughness across the seam and fades it out within 150 m, so
    the first generated row matches the last real row.
- **Seam:** any generated grid vertex lying on a real tile's boundary **copies that tile's
  quantised height**. `S + D` already comes within millimetres there; the copy makes the seam
  bit-identical.
- **Water:** river cells where `|S + D| > 0.5 m` are not classified `Water`. A blended river bed
  is sloped, and water would render on the slope.

### 4. Invariants the maths must keep

These are what make it seamless without cross-tile coordination. Each one gets a check in
phase 5.

1. **Generated|generated seams are bit-identical.** Both tiles must see the same real tiles in
   the same order:
   - each tile's `Near` list takes real tiles within `Band + 100 m` of its square, sorted by
     (N, E);
   - any real tile within `Band` of a point on a shared edge is within `Band` of both squares;
   - window: ±4 tiles.
2. **Coarse = decimated full.**
   - `S` is sampled on a world-anchored **10 m lattice** per tile. Full grids interpolate it;
     coarse grids (stride 10) and the horizon (100 m) land exactly on lattice points, where
     interpolation returns the stored value unchanged.
   - `D` is computed per vertex. At 10 m points the real coarse grid equals the full grid.
   - At 100 m knots `det = 0` exactly, so the horizon needs no real grid.
3. **Generated|real seams are bit-identical.** By the vertex copy.
4. **The point path equals the grid path.** Trees, roads, buildings and cover go through the same
   `Ground(site, e, n)` as the grid: inside T, lattice plus D; outside T, exact S plus D.
5. **Edge profiles of G.** `G(q)` on a real tile's edge comes from a cached 1 m profile per
   (real tile, edge). It is built from 1-D noise along the edge, with arithmetic identical to
   `Raw`'s bilinear at u = 0 or v = 0, so it is bit-equal to `Raw`.

### 5. Where the real data comes from

`BlendFor(T, full)` is async, cached per (T, full, Version), LRU 64. It builds `RealTile`
records for `Near`:

- **Knots (100 m):** from the real `horizon.bin` if loaded; otherwise
  `HorizonFormat.Extract(coarse grid)`. The values are identical either way.
- **Grids, for the 8 neighbours only:**
  - edge-adjacent tiles, when T is requested at full resolution: the **full** grid;
  - everything else: the **coarse** grid. Diagonal tiles only ever contribute their corner,
    which the coarse grid holds.
- Loads go through the outer `CachingChunkSource` (`FallbackChunkSource.Neighbours`), so a real
  grid decoded for the blend is shared with the loader. They can't recurse: a real id is never
  covered by the fallback.
- A real tile listed but missing on disk or on the server is skipped. It leaves a hole, as it
  does today.
- Generated knots per real tile (`GKnots`) and G edge profiles are cached globally. They depend
  only on the generator, not on the real set.

### 6. Loader integration (`ChunkManager`)

`UseFallback(FallbackChunkSource, invalidate)` replaces the current API. `RetireFallback` and
`FallbackActive` go.

- **`MergeAvailableTiles`:** collect the newly real ids, then:
  1. `fallback.SetReal(_available)`
  2. `affected` = new ids ∪ generated tiles within 4 of them.
  3. Unload every loaded state in `affected` (cancelling builds).
  4. `invalidate(affected)` on the cache.
  5. `Horizon.Reload()`, which keeps coverage.
  6. Bump `_worldVersion`, which feeds `_orderedKey`.
  7. Raise `TerrainReplaced(affected)`.

  Unloading rather than re-committing in place is deliberate: a commit whose roads, buildings or
  trees are null does not clear the old ones, so an in-place rebuild would leave generated houses
  standing on real ground.
- **`ResetAll()`**, for the rebase path: unload every state, clear the cache, the fallback caches
  and the horizon (`Clear` + `Reload`), then raise `TerrainReplaced(null)`.
  `ClientTerrainSync.Adopt` calls it before `Rebase` when the client has no real tiles.
- **`FitHorizonCoverage`** covers the real set plus the fill domain, so generated tiles get
  coverage texels.
- **`TerrainReplaced`** carries the affected set:
  - `ClientWorld` calls `Forget()` on its caches as today;
  - it calls `RequestReplacement()` only when the player's tile is in the affected set.

  It fires on every merge now, and snapping a moving player to the ground over an unrelated
  merge would be a bug.

### 7. Invalidation

- **`CachingChunkSource.Invalidate(ids)`:** removes every slot for those ids and bumps the
  epoch, so a fetch straddling the call is not cached.
- **`ProceduralWorld` cover cache:** keyed by (id, blend version). The noise-lattice cache is
  unaffected, since it doesn't depend on the real set.
- **`HorizonLayer.Reload`** gains a queued re-run. Today a reload requested while one is in
  flight is dropped, and a merge during boot would leave a horizon generated from the old real
  set.

### 8. Horizon

`FallbackChunkSource.LoadHorizonAsync`:

1. Load the inner real index.
2. Keep it for the blends.
3. Generate 100 m samples for the non-real tiles in the fill domain plus 60 tiles, using a
   knots-only blend, with the exact `S` at 100 m points; boundary samples copy the real knot.
4. Merge both into one `HorizonIndex`.

Parallel, on a worker. Estimate for a full Swiss region: ~230k generated tiles × 121 samples,
about 1 s. Without a real index there is no blend in the far horizon. It's acceptable, because
covered tiles discard the horizon anyway.

### 9. Content in the band

- Cover, trees, buildings and roads all read heights through `Ground`, so forest follows the
  blended slope and houses re-seat on it.
- `Solid` already refuses footprints spanning more than 4.5 m, which keeps villages off
  blend-steepened ground.
- Nothing generated is placed in a real tile: owners are generated tiles only.
- Roads are clipped per generated tile, so they dead-end at the border, as agreed.

### 10. Server

- `ServerWorld` wraps its `LocalChunkSource` in the same `FallbackChunkSource`, anchored and
  domained identically, with the real set from its manifest.
- Its grid-only `ChunkManager` then answers height queries on generated ground.
- `InteriorManager` and `LootService` read generated buildings through the same source, so
  interiors and loot are planned server-side for generated houses. Building keys are
  deterministic (tile + index).
- Clients never request generated tiles over the network: `Covers` short-circuits before
  `NetworkChunkSource`.
- **Open:** see question 1 below.

### 11. Performance budget

Measured basis: a full generated tile takes 26–40 ms and cover ~10 ms (steady state).

| Stage | Estimate per tile |
|---|---|
| `S` lattice (10k points × up to ~60 near tiles) | 10–25 ms |
| `D` (≤ 8 tiles, only vertices within 150 m of a real border) | 5–15 ms |
| `BlendFor` loads | cache hits after the first; one full real grid per edge neighbour (2 MB, already needed by the loader when the player is near) |

A tile not near real terrain costs exactly what it does today. Target: no `--fly` frame over
33 ms, and the ground under the player within 1 s of arriving at a border.

## Phases

Each phase ends green on its checks. File lists are exclusive per phase, so phases can go to
separate agents.

**Phase 1: blend maths, standalone.** `src/Terrain/ProceduralWorld.Blend.cs` (new), plus
`ProceduralWorld.cs` and `.Settlements.cs`, which thread `Site(tile, noise, blend)` through every
height query: `Ground`, `BuildGrid`, `ClassifyTile`, `BuildTrees`, `Solid`, `BuildRoads`,
`FarmsNear`, `CoverAt`, `BuildHorizon`. Contents:
- `RealTile`, `Blend`, `CreateBlend`;
- the S lattice and exact S, D, and the edge profiles;
- the vertex copy and the water suppression.

No loader changes yet: `blend = null` reproduces today's output byte for byte.

**Phase 2: the source.** `FallbackChunkSource.cs`:
- the snapshot (real set, domain, version), `SetReal`, `Covers`;
- `BlendFor` with neighbour loads, and the merged horizon.

Plus `CachingChunkSource.Invalidate`.

**Phase 3: the loader.** `ChunkManager.cs`:
- `UseFallback`, `IsAvailable`, the merge path, `ResetAll`, `_worldVersion`,
  `FitHorizonCoverage`;
- the `TerrainReplaced(affected)` signature.

Plus `HorizonLayer.cs` (the queued reload) and `ClientTerrainSync.cs` (`ResetAll` before
`Rebase`).

**Phase 4: hosts.** `ClientWorld.cs`:
- fixed anchor, `--generated off`;
- the replacement handler, only for the player's own tile.

`ServerWorld.cs`: the fallback source; then question 1.

**Phase 5: verification and docs.** The checks below, plus updates to `CLAUDE.md`, `README.md`
and `SETUP.md`.

## Verification

- **Standalone harness** (scratch console project compiling `ProceduralWorld*.cs` against
  `TerrainFormat`, as used for `84f5ebf`), with a synthetic "real" terrain: a 3×3 block and a
  one-tile hole in a 5×5 block, both far above and far below the generated heights.
  - generated|real edge: 0 mismatching vertices, full and coarse;
  - generated|generated edges across the whole band: 0 mismatches;
  - coarse vs decimated full, for blended tiles: 0 mismatches;
  - horizon samples vs grid at 100 m: 0 mismatches;
  - continuity: the largest height step between adjacent vertices across the band must not
    exceed the largest step inside the real or generated terrain alone (no cliffs, no hole-axis
    jump);
  - `blend = null` output byte-identical to `84f5ebf`;
  - timings per stage.
- **In game:** a real region assembled from the chunk cache into `test_output/` (20 tiles around
  Riddes, as before) as `--chunks`.
  - `--shot` across the border from above and at eye level;
  - `--ride foot` and `--ride bike` across it: clearance ≈ 0, no fall-through;
  - `--fly` along and across the border at 150 m/s: no frame over 33 ms;
  - `--perf full`: tile latency near the border.
- **Server and join:**
  - dedicated server on that region, a client with no terrain: rebase, the merge rebuilds only
    affected tiles, the generated world around the region stays;
  - a server-side height query on generated ground (e.g. `/tpall` to a point outside the region)
    lands on the ground;
  - an interior entered in a generated house on both peers matches.
- **Quit** with builds in flight, 3 runs: exit 0.

## Risks

- **Cross-machine determinism.** `Math.Sin`, `Exp` and `Log` are not guaranteed bit-identical
  across OS and CPU runtimes. Client and server can therefore differ by one quantisation step
  (7 cm) on rare vertices. Nothing may rely on exact equality of generated heights across peers:
  placement tolerances already exceed that, but it rules out, for example, hashing heights into
  keys.
- **Different real sets on two peers.** A client with local tiles the server lacks will blend
  differently near them than the server does. It's harmless visually; server height queries
  there may be off by the local blend. The fix would be "the server's real set wins in
  multiplayer", which is not planned.
- **Memory.** Blend contexts hold references to neighbour grids (up to 4 full grids of 2 MB for
  an edge tile). LRU 64 bounds it at ~500 MB worst case, so it may need to be LRU 16, or to drop
  grids after the build.
- **Large merges.** A server index of 6,699 tiles merged at join makes `affected` about 540k ids.
  That's one pass on the main thread; measure it, and move the dilation to a worker if it is
  over 20 ms.
- **The steep band.** Where a 3,000 m real peak meets the generated valley floor, the band holds
  a 37° wall with forest and rock bands on it. It's plausible, but worth a screenshot review.

## Open questions

1. **A server with no real terrain at all.** Today it refuses to start ("nothing to serve").
   With generation on the server it could start with a generated-only world, origin at the
   anchor. **Recommendation:** allow it behind `--generated-world`, keep the refusal as the
   default. A server that silently serves an invented Switzerland is surprising.
2. **Persisting the setting.** `--generated off` is enough for the checks. Should it also be a
   Settings entry (`GameSettings.GeneratedFill`)? Recommendation: yes, in phase 4, since it's
   cheap.
3. **Visual treatment of the border.** A generated area looks different from swisstopo data
   (flat vertex colours, no surveyed buildings). Should generated tiles be marked, e.g. a faint
   tint or a HUD note ("generated terrain")? Recommendation: a HUD note only.

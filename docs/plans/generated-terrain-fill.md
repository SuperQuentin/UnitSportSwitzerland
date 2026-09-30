# Generated terrain fill: generated and real tiles side by side

Status: **phase 1 done, phase 1b (blend quality, at the end of this file) next** (issue #27,
branch `feat/27-generated-terrain-fill`). Builds on `84f5ebf` (generated fallback world,
all-or-nothing).

### Phase 1 as built (differs from the text below in three places)

- **IDW softening is 0.01 m², not 1.** With `d² + 1` a second real tile 5 m away still took ~4% of
  the weight at the seam, so a concave corner came out up to ~20 cm off the real edge one row in.
  At 0.01 the weights interpolate: S + D is within **1.3 cm** of the real edge before the copy.
- **Water is suppressed by the correction's gradient (> 1.5%), not its size (> 0.5 m).** S is
  non-zero across the whole band, so the size test removed every river within 3 km of real ground,
  flat beds included. The intent was "no water on a tilted bed"; 1.5% leans a 22 m bed 0.3 m.
- **No G edge profile**: D memoises its whole `det` per whole metre of the tile's own boundary
  (from inside the tile the nearest real point is always there), per detail tile, computed from
  `Height(e, n)` itself, so bit-equality with `Raw` holds by construction.

Checked by `dotnet run --project tools/BlendCheck -c Release` (synthetic real blocks up to 3,400 m
off the generator): 0 mismatching vertices on generated|real and generated|generated seams at both
resolutions, coarse = decimated full, horizon = grid at 100 m, point path = grid path on 1.3 M
vertices; no step over the band's bound, none in the one-tile hole over generated ground; trees
and roads within 1 m of the mesh. `blend = null` was checked byte-identical to `origin/main`
(grids, cover, trees, roads, buildings on 16 tiles; all 40,401 horizon tiles) with a one-off
harness. Cost: a blended full grid ~50 ms against ~23 ms unblended (single thread), coarse ~4 ms,
a horizon tile 0.1 ms.

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

## Phase 1b: blend quality (before phase 2)

Phase 1 passes every numeric check, but shaded-relief renders of the BlendCheck world show three
artefacts that no check measured. Renders: `test_output/blend/*_before_after.png` (left the
generator alone, right blended), made by the phase-1 scratch harness. They are to be regenerated
by BlendCheck itself (see below).

### What the renders show

1. **Streaks across the whole band.** `sm_k` is read at the nearest point of the real tile, so it
   is constant along every line perpendicular to the edge. Real relief between ~100 m and ~1 km
   (ridges, gullies) is therefore extruded straight out for the full 3 km, which shows as radial
   smearing round the low block. The plan's claim that the 100 m knot lattice "stops real fine
   detail being extruded as streaks" holds only for detail finer than 100 m.
2. **A comb at the edge.** D extrudes the real residual the same way for 150 m, so the real
   ground's fine ripple turns into parallel stripes running away from the seam.
3. **Creases.** S is piecewise bilinear on the 100 m knots, so its slope jumps every 100 m. It
   also jumps along the lines that continue a real tile's edges, where the nearest point switches
   from an edge to a corner. Both show as straight folds in hillshade, carried outward for 3 km.

The synthetic terrain exaggerates all three: it is pure sine waves, including a 4 m ripple at a
9 m wavelength. Real ground would still show them, e.g. a gully extended 3 km into generated land.

### Fix A: S smooths more the further it reaches

The width of real detail S may carry grows with the distance it is carried, so a feature
extruded d metres is never narrower than about d/2 and reads as broad shape, not a streak.

- Per real tile, keep a **pyramid of `sm = Rs − Gs`** built from its 11×11 knot differences:
  level 0 = the 100 m knots themselves, then area-averaged levels on 200 m (6×6), 500 m (3×3) and
  1000 m (2×2, the corners) lattices, and a last level holding the tile's mean (one value).
  Area-average (trapezoid weights over the 100 m knots), never decimate: decimating aliases, which
  is the streak again at a coarser pitch.
- At distance d, sample `sm_k(q)` from the level whose spacing is about `max(100, d/2)`: 100 m up
  to d = 200 m, then 200 m at 400 m, 500 m at 1 km, 1000 m at 2 km, the mean at 3 km. Interpolate
  between the two adjacent levels with a smoothstep in log-distance, so no level switch is a step.
- At d = 0 this is exactly level 0, i.e. **today's S at the seam**. The vertex copy, D and every
  seam invariant are untouched.
- Creases fade with distance for the same reason: the coarse levels are nearly planar, so both
  their 100 m kinks and the jump where the nearest point turns a corner shrink as d grows. Within
  ~300 m of the seam, level 0 still creases, but there the real texture that D carries dominates.
- The 10 m S lattice stays, so coarse = decimated full and horizon = grid still hold by
  construction. The pyramid depends only on the real tile and the generator, so it is cached with
  `GKnots`. Cost: two level lookups per (point, tile) instead of one, lattice ~1.8 → ~4 ms.

### Fix B: carry real detail across the seam smoothly, and not far

The earlier idea, **mirroring** the real texture (take D at `m = 2q − p`, as far inside the real
tile as p is outside it), is **rejected**. It keeps the seam exact and avoids stripes, but a
mirror flips the slope: a residual rising toward the edge falls away beyond it, so every seam
would carry a ridge or a gully (C0, not C1). Its anti-symmetric variant, `2·det(q) − det(m)`,
fixes the slope but doubles the extruded `det(q)`, which is the streak again.

Instead:

- **Continue the real residual to first order and fade it quickly**:
  `D = Σ w'·fd·(det(q) + d·∂det/∂n(q)) / Σ w'`, with `∂det/∂n` the outward normal derivative of
  `det` at q, a one-sided difference on the real grid (1 m for edge neighbours, the corner
  vertex's 10 m difference for diagonals). With a smoothstep fade, value and slope both match
  the real side, so the seam is C1 and neither ridged nor creased.
- **`DetailBand` 150 → ~40 m.** The comb is extrusion, and a short fade leaves it no length to
  show. Past ~40 m the generator's own texture has fully taken over. Tune it by render: the
  shortest band that does not show a change of texture along the seam.
- `det(q)` and `∂det/∂n(q)` depend only on q, which from inside the tile is always a whole metre
  of its own boundary, so the per-edge memo stays and D gets cheaper, not dearer.
- The horizon still needs no real grid, **as long as `DetailBand` stays under 100 m**. Real tile
  edges lie on the 100 m lattice, so a 100 m point is either on a real edge (d = 0, where D is
  `det(q) = 0` at a knot and the vertex copy applies anyway) or at least 100 m from every real
  tile, where the fade is already zero. The slope term never gets a 100 m point to act on.
  Pin this with a comment on the constant: a band of 100 m or more breaks horizon = grid.

### Verification additions (BlendCheck)

- `--render` writes the three before/after hillshades to `test_output/blend/` (the overview, the
  one-tile hole and a block corner), so every change to the blend is judged by eye as well.
- **Seam kink**: at every generated|real edge, the change of slope across the seam,
  `|(h₁ − h₀) − (h₀ − h₋₁)|`, compared with the same measure one row in on either side. Mirroring
  would fail it; Fix B must pass it.
- **Streak metric**: along lines parallel to a real edge, 500 m to 3 km out, the high-pass
  variance of the blended height minus that of generated ground alone. Extruded real detail shows
  as excess variance at the real tile's wavelengths. Fix A should drive it near zero past ~1 km.
- Every phase-1 check stays green, `blend = null` included.

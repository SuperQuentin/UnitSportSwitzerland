# Network stage: blocks at once (#1539)

`TileRewriter.Run(ITileLoop, ...)` runs its blocks at once, byte-identical to one after another: up to `BlockJobs`
at a time, each on a **new thread of its own** with **its own `Session`**. The thread writes its block's finished tiles
itself (memory is bounded by the blocks in flight), then its session waits with its numbers until the blocks before it are
done: `Session.Absorb` adds them up in block order, so the log and `Stats` do not depend on who finished first.

## Rules for code in the stage

- **Nothing on `Session` or `NetworkStats` is shared between blocks.** A new counter just works: `StatsMerge` adds fields by
  reflection (numbers sum, `Max*` keeps the larger, lists append, dictionaries add per key, nested stats recurse) and **throws** on a
  field type it has no rule for. A list of first examples takes `[StatsSample(n)]`. A number that depends on the order (a "worst at") goes
  in an `Absorb` by hand, strict `>`, so the first of equals stays.
- **A stats field never decides output.** Comparing with itself inside one call (`stats.Gaps > gapsBefore`) is fine.
- **Everything on `Inputs` is read-only once `Run` starts** (`OsmOverlayReader`, `SignalSites`, `Restrictions`, `CrossingNodes`,
  `Cantons`, `GantryContext` but for its `ConcurrentQueue` of facts, sorted when written), and the `Func`s on it must be safe on
  several threads. `TileRewriter.UncontrolledTramCrossings` is set once by `Lv95TileLoop`'s constructor, before any block.
- **A cache is per block** (`Footprints`, `Facades`, `UrbanField`, the ground), or a `ConditionalWeakTable` keyed by a block's own object
  (`CapsOf`, `MotorwayPoints`, `UnlitStages`, `ClearOfBands`). No static collection fills up during a run.
- **A `[ThreadStatic]` is set before it is read, in the same block.** `Plane.Use` is per thread: `Run` sets it on each block's thread,
  and a new thread you start inside a block would be on LV95 again. `_stopBack`, `_crosswalkArms`, `_exitBikeFrom` are set by `PlanStopBacks`, and their
  only readers are after it (`EmitTurnLanes`, `EmitSignals`, `EmitDataCrossings`, `WriteLayouts`); `_ramps`, `_pathLines` are emptied after their
  writers; `CornerPlanner`'s are set at the top of its one entry, `Plan`. A new thread per block makes a stale value impossible, not just unlikely.
- **No file a block reads may be one another writes.** A target with no raw input breaks that (`LoadInput` stashes it, `--force` reads
  it back rewritten), so `Lv95TileLoop.BlocksIndependent` is false there and the blocks run one by one. So do a shared `Inputs.Facades` (the
  generated world's `Rewrite`, one call at a time as before) and the diagnostics that append to a file or trace (`--debug-street`,
  `--debug-junction`, `--junction-list`, `--junction-failures`). The `*DBG` environment switches still print, interleaved: use `--jobs 1`.
- `ITileLoop.Write` runs on the block's thread, one block's tiles at a time.

## Jobs and memory

`--jobs N` on both entries (`RoadGen --rewrite`, the preprocessor: `JobsFromCommandLine`), `Options.Jobs`. Default:
`min(cores, (TotalAvailableMemoryBytes - heap in use) / 2 GB)`, so `DOTNET_GCHeapHardLimit` is obeyed by itself. An explicit `--jobs` is not
bounded. Zurich below: its 12 blocks one by one pass under a 2 GB limit; 10 at once under a 6 GB limit run **out of memory**; the default gives one
at 6 GB, 7 at 16 GB, 12 on 32 GB (peak 10.5 GB).

`Lv95TileLoop`'s blocks are 6 km squares, so a single dense block sets the time of a small region: Geneva's 24 tiles are most of the benchmark's stage.

## Measured (6-core 5600X, Nov 2026)

| Run | Blocks | Wall | CPU | Cores | GC pause | Peak | Identical |
|---|---|---|---|---|---|---|---|
| Benchmark `roads/network`, baseline | 3 | 189.5 s | 204.8 s | 1.08 | 1.85 s | 4273 MB | reference |
| ... serial fixes, one thread | 3 | 85.7 s | 100.0 s | 1.17 | 1.58 s | 4050 MB | 557 of 557 |
| ... at once | 3 | 69.6 s | 113.4 s | 1.63 | 1.64 s | 4513 MB | 557 of 557 |
| Zurich standalone, 216 tiles, baseline | 12 | 481.6 s | 480.8 s | 1.00 | not measured | 3946 MB | reference |
| ... at once, workstation GC, 10 jobs | 12 | 83.4 s | 329.0 s | 3.95 | 17.83 s | 10069 MB | 217 of 217 |
| ... at once, Server GC, 12 jobs | 12 | 65.5 s | 361.8 s | 5.52 | 4.79 s | 10536 MB | 217 of 217 |
| ... 16 GB heap limit, 7 jobs | 12 | 70.4 s | 329.3 s | 4.68 | 4.71 s | 11004 MB | 217 of 217 |

The serial fixes, each proved alone on the benchmark: `CorePlanner.Outside` parity first (189.5 -> 172.3 s), `RailRoadOverlap.ZoneIndex`
(-> 144.0 s), `Footprints` array grids and no cache of asked points (-> 85.7 s). `DOTNET_GCgen0size=1 GB` gave 64.4 s against 65.5 s for 2 GB more peak: not kept.

`--rewrite` alone does not write the preprocessor's bytes: it also passes `places.json`, `tram_stops.tsv` and `sitg_signs.tsv` (31 of the benchmark's 52 `.road`
differ, gantry destinations), and it finds `switzerland.bin` only from a checkout (run it elsewhere: no cantons, other signal heads). Compare like with like.

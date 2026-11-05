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
`min(cores, (TotalAvailableMemoryBytes - heap in use) / 2 GB)`, so `DOTNET_GCHeapHardLimit` is obeyed by itself. An explicit `--jobs` is held to
the same bound (lowered, with one log line); before that, it was not. Zurich below: its 12 blocks one by one pass under a 2 GB limit; 10 at once under a 6 GB limit run **out of memory**; the default gives one
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
| ... 6 GB heap limit, `--jobs 10` lowered to 2 | 12 | 149.7 s | 323.4 s | 2.16 | not measured | 5520 MB | 217 of 217 |

The serial fixes, each proved alone on the benchmark: `CorePlanner.Outside` parity first (189.5 -> 172.3 s), `RailRoadOverlap.ZoneIndex`
(-> 144.0 s), `Footprints` array grids and no cache of asked points (-> 85.7 s). `DOTNET_GCgen0size=1 GB` gave 64.4 s against 65.5 s for 2 GB more peak: not kept.

`--rewrite` alone does not write the preprocessor's bytes: it also passes `places.json`, `tram_stops.tsv` and `sitg_signs.tsv` (31 of the benchmark's 52 `.road`
differ, gantry destinations), and it finds `switzerland.bin` only from a checkout (run it elsewhere: no cantons, other signal heads). Compare like with like.

## Where the stage's memory goes (#1563, Nov 2026)

Measured on the multi-batch region (477 tiles, 20 blocks, 12 at once, Server GC) with temporary probes (not kept): a forced blocking gen2
every 3 s, one at every phase of a block with `--jobs 1`, and `GC.GetAllocatedBytesForCurrentThread` per phase. The stage's 8.3-8.6 GB peak working
set is **not** blocks holding 2 GB each:

| What | Size | Note |
|---|---|---|
| Live before the first block | 0.52 GB | `Inputs` and what earlier stages left; held once |
| Live per block, densest | +0.5 GB | roads +4, lines and graph +70, junction pipeline +210, layouts and emit +410, tiles +420 MB; the 8x8 ground grids are 128 MB of it |
| Live per block, typical | +0.1 to +0.3 GB | back to +13 MB when the block is written: a finished session holds numbers only |
| Live at the peak (11 blocks) | 4.3 GB | = 0.5 shared + blocks in flight |
| Dead, waiting for a GC | 2.9 GB | about 1.8 GB gen0/gen1 (the 12 heaps' budgets), 1 GB dead large-object arrays (finished blocks' grids), 0.2 GB gen2 |
| Committed at the peak | 7.5 GB | the process working set stays near 8 GB for the rest of the stage |

- 56-60 GB are allocated in the stage's 25 s. `CornerPlanner.Plan` (tile assembly) allocates 24 GB of it (50 MB a tile of lists, tuples and LINQ),
  `PlanAndMerge` 20 GB, the lines and graph 4.7 GB, `WriteLayouts` 3.4 GB. There is no big array reallocated per tile to pool; the garbage is small objects.
- The heap runs to about 7.5 GB between full GCs (3 gen2 and about 40 gen0 in the stage). Fewer blocks do not cut it in proportion: `--jobs 3` still
  peaks at 5.7 GB in the stage (45 s), because the budgets belong to the heaps, not the threads.
- The 2 GB a block (`BlockMemoryBytes`) is 4 times what a block of this region holds live, but it is the bound the denser Zurich blocks were found
  under (10 of them at once failed at 6 GB), and it only matters under a heap limit. Not changed; the Zurich blocks were not re-measured.
- **The run's peak is not always the network stage's.** Benchmark: the network stage (4.5 GB). Rural: the water stage (8.1 GB; network 5.8 GB, cover 5.9 GB).
  Multi-batch: the network stage and the cover stage after it, which starts on top of the network stage's committed memory and reaches 8.6 GB in the
  pipeline against 6.0 GB (cover) and 7.3 GB (water) in a fresh process (`--cover-only`). Lowering the network stage alone lowers the multi-batch peak
  by what the cover stage then falls to, and no further than the water stage's 7.3 GB.

Tried, byte-identical in every case, none kept (network stage on the multi-batch region, 24.3-25.7 s and 8.3-8.6 GB in five unchanged runs):

| Change | Stage wall | Stage peak | Verdict |
|---|---|---|---|
| Ground tiles decoded when first sampled (halo tiles with no road were decoded and dropped) | 24.8 s | 8.27 GB | -0.2 GB, within the spread |
| ... and the block's height arrays pooled (`ChunkCodec.Decode` into a rented array, back at the block's end) | 26.9 s | 7.98 GB | -0.4 GB, 5 %, for a shared codec change |
| `DOTNET_GCgen0MaxBudget=256 MB` (env only: `System.GC.Gen0MaxBudget` in the runtime config is ignored) | 25.1 s | 8.46 GB | nothing |
| `DOTNET_GCgen0MaxBudget=64 MB` | 26.9 s | 6.81 GB | -1.7 GB, +11 %; the cover stage then peaks at 8.3 GB |
| `System.GC.HeapCount=6` (a fixed count suits only this CPU) | 25.9 s | 7.04 GB | -1.5 GB, +6 %; cover then peaks at 8.7 GB |

Under a hard heap limit (`DOTNET_GCHeapHardLimit`, default jobs, which the 2 GB bound lowers): 16 GB passes (7 at once, 28.5 s in the stage), 8 GB passes
(3 at once, 69 s), 4 GB passes (1 block, 100 s in the stage, 4.2 GB peak, 190 s the run). **The network stage itself completes in 2 GB**, one block at a
time (103 s, 2.2 GB); what stops the run is after it: at 3 GB `cover/pave` ran out of memory (once; a 1 GB bound with the same one block passed
at 3 GB, so it is a coin flip there), at 2 GB loading the next 400-tile batch of grids (`cover/load`, 0.8 GB of grids) does. A 1 GB bound gives 2 blocks at 4 GB, which
then ran out of memory in `cover/pave` where 1 block had passed: a lower `BlockMemoryBytes` buys time under a limit, not a lower limit. Linux and a nationwide run were not tried.

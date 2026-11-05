# Cover and water stages: per tile, all cores, byte-identical (#1539)

How the two stages run, what must stay true when they change, and what was measured. The network stage has its own note (`roadgen-parallel`).

## Rules for code in the cover stage

- **A cell's value depends only on the order of the rings that paint it.** `CoverExtractor` reads rings one thread in source order
  (`Rasterise` queues them, 256 MB of doubles at most), `PaintPending` hands each tile its rings in that order and paints tiles at once. Later
  rings overwrite earlier ones; Water stays (`PaintRing`). So a tile's list keeps source order, and painting never crosses tiles.
- **`PaintRing` is in whole metres.** It relies on `ChunkFormat.SpacingM` being 1: sample rows and columns are integers, a tile's lattice holds both its
  edges, an edge counts for rows `lo <= N < hi` (half open). The crossing formula is the old one, term by term: its rounding decides a cell at a polygon's edge.
- **Trees: one tile, one list, one generator.** The `Trees` lists exist before the parallel passes (`ScatterTrees`), and the dictionaries are read-only inside them.
  List order per tile stays scatter, rows, surveyed (one thread, source order), car park planters. A generator's calls per tile must not change.
- **`UrbanField` and `Facades` cache and are not thread-safe**: the paving pass makes one of each per 4 km block (`PaveBlock`), a pure function of the walls, dropped with
  the block. That costs CPU (each block loads the walls round it; pave CPU 2.9 -> 6.3 s) and it is the one place left with visible waste; a shared
  field would need a thread-safe `UrbanField` in RoadGen.
- `--jobs` reaches both stages. Cover needs no memory bound: the tile arrays exist anyway, the workers hold a few MB.

## Rules for code in the water stage

- Phase C reads only the cache and its own tile, so tiles can run in any order. Report numbers (`OutDepthSum`, `worstJoin`) add up in thread order: they go to the log, never to a file.
- **Three columns of input stay the bound for tall columns** (`water-pass-memory`). Only short columns join into a group, until `2 x workers` tiles.
- A tile's window takes about 250 MB; workers are `(available - heap) / BedWindowBytes`, 500 MB each, so `DOTNET_GCHeapHardLimit` lowers them, an explicit `--jobs` too.
- **Window arrays come from `WaterWindow.Pool`** (#1557): a tile's ~30 W*W arrays (5-39 MB, large object heap) are rented zeroed and returned, never allocated per tile. A new window-sized array in `ComputeTile` or in a `WaterWindow` method takes `pool` too, or the GC again holds many times the live set.
- Identical minimum and distance, faster walks only: `RefineWet` (van Herk), `DistanceTransform` (a row at a time).

## Measured (6-core 5600X, warm, Nov 2026)

Benchmark 52 tiles and rural region (`builder-bench`). Before is the binary after the RoadGen work. The rural region is 216 mountain tiles: forest, rock, scree, glacier and the big polygons they come in, 2.4 % water, no survey.

| Run | Stage | Wall | CPU | Cores | GC pause | Peak MB | Identical |
|---|---|---|---|---|---|---|---|
| Benchmark, before | whole run | 155.6 s | 278 s | | 4.7 s | 5033 | reference |
| ... after | | 100.3 s | 234 s | | 4.8 s | 7573 | 557 of 557 |
| ... before | cover | 55.7 s | 57.2 s | 1.03 | 0.15 s | | |
| ... after | | 2.3 s | 8.2 s | 3.60 | 0.15 s | | |
| ... before | water | 6.7 s | 25.7 s | 3.87 | 1.21 s | | |
| ... after | | 3.9 s | 26.6 s | 6.77 | 1.05 s | | |
| Rural, before | whole run | 91.7 s | 307 s | | 3.6 s | 7112 | reference |
| ... after | | 58.0 s | 274 s | | 3.7 s | 9283 | 1931 of 1931 |
| ... before | cover | 35.7 s | 37.4 s | 1.05 | 0.17 s | | |
| ... after | | 2.7 s | 15.5 s | 5.72 | 0.13 s | | |
| ... before | water | 5.8 s | 42.2 s | 7.26 | 0.87 s | | |
| ... after | | 4.6 s | 29.5 s | 6.45 | 0.84 s | | |

Same bytes at `--jobs 1` and `--jobs 3`, with `DOTNET_TieredPGO=0` and `DOTNET_TieredCompilation=0`, with a 16 GB and a 6 GB heap limit, and with rings painted every 1,024 coordinates
(the bound on queued rings forced to flush mid-layer). What is left of a run is the network stage (69 of 100 s, 20 of 58 s), then terrain and the constant three.

The gains, each proved alone on both regions: per-tile painting (benchmark cover/extract 51 -> 0.65 s, rural 27.8 -> 1.9 s), all cores for the rest of cover, then water.

## Multi-batch region (Lausanne to the Vaud Alps, 477 tiles, 2 batches, 20 blocks, Nov 2026)

E 2538-2567 x N 1136-1152 (510 listed, 477 with a source), Léman with its survey, Vevey, Montreux, the Alps. Identical bytes (4383 of 4383) against `main` at default jobs, at `--jobs 3` and with the cover, trees and network running across both batches.
`main`: 404.5 s, 21 GB; this branch before #1557: 143.7 s, 24.2 GB (12 blocks at once). `water/C-beds` was the peak (8.6 -> 24.2 GB, 12 workers) and a 16 GB heap limit ran out of memory in it.

**Measured (#1557)**: forced blocking gen2 collections between the groups of columns showed the live set flat at 1.0-1.1 GB (about 0.65 GB left by earlier stages, 45 MB of survey, 40-55 cached input tiles, the rest the group's windows),
the GC's committed heap at 2.5-3.5 GB, and the process's committed memory (`VirtualQuery`, private) growing 3.5 GB per group, 7 -> 42 GB over the stage (the working set stopped at 23 GB, the rest paged out). So `BedWindowBytes` was right
about live data (250 MB a window): the excess was **dead large-object garbage in freed but still committed regions**, which the GC's own commit figure leaves out. Each tile allocated ~70 MB (no survey) to ~450 MB (survey) of
window arrays; Server GC with 12 heaps collects the large object heap rarely and returns the regions late, and under a heap limit the freed 4 MB regions cannot serve 19 MB arrays. Not the `held` cache, the survey kilometres or a stale `HeapSizeBytes`.
Fix: a pool of exact-length arrays, rented zeroed (same as `new`), returned when a tile is done. The pool keeps about 190 MB a worker, for phase C only: it is a local of `WaterStage.Run`, dropped before the stitch (the stage also runs in-process in the game's map builder).

| Multi-batch, 477 tiles | Before (#1540) | With the pool |
|---|---|---|
| `water/C-beds` wall / GC pause | 38.1 s / 11.1 s | 24.7 s / 0.7 s |
| `water` wall | 43.0 s | 27.7 s |
| Peak working set | 24.2 GB | 8.9 GB |
| Whole run | 143.7 s | 100.5 s |
| 16 GB heap limit | out of memory in `C-beds` | completes, 107.8 s, 8.9 GB |

Identical bytes (4383 of 4383) at default and at 16, 12, 8, 6 and 4 GB heap limits. Whole run / `water/C-beds` wall / peak working set: 12 GB 111 s / 24.9 s / 9.2 GB; 8 GB 121 s / 24.8 s / 7.8 GB; 6 GB 178 s / 27.0 s / 6.2 GB; 4 GB 188 s / 35.8 s / 4.2 GB
(the bound leaves 6 then 4 workers). 2 GB runs out of memory in `RoadCodec.ReadFloats`, in the cover stage after the roads: the water stage is not what stops it.

## Water bed phase: where the time goes, and what SIMD did (#1566)

Per-function stopwatches (summed over the 12 workers, a run of the multi-batch region before any change; `C-beds` CPU was 231 s, the stopwatches add up to 257 s because a descheduled thread keeps counting):

| Step of `ComputeTile` | Thread-seconds | Share | Vectorisable exactly? |
|---|---|---|---|
| Survey sampling (`BathySource.SampleBed`, 898 million calls) | 88 | 34 % | no: double divisions, floors, branches, grid lookups |
| `RefineWet` (row minimum 11, column scan 26) | 37 | 14 % | column scan yes (integer min); row minimum is a serial running min |
| `DistanceTransform` x 2 (columns pass 19, rows pass 26) | 48 | 19 % | columns pass yes (integer select); rows pass no (double parabola envelope, data dependent stack) |
| `SmoothEdgeDepth` (box tables 17, rest 2) | 19 | 7 % | the tables are prefix sums, mostly memory writes |
| Vertex loop (`HalfWidth`, `Depth`, quantise; 13 without per-vertex timers) | 13 | 5 % | no: pointer chasing, per-vertex branches |
| Window fill and array rental | 15 | 6 % | memory bound |
| `ReadCache`, grid decode, join steps, minmax | 11 | 4 % | no |
| `ChunkCodec.Encode`, `WaterFormat.Encode`, file writes (zlib inside) | 3 | 1 % | not ours |

So zlib and file I/O are about 1 % of the phase: the phase is arithmetic and memory traffic over windows of 4.8 million cells (20 to 40 MB per array, twelve tiles at once on six cores).
The share a vector can reach exactly was about 24 % (column scan, columns pass, box tables), so the ceiling for a 4x loop speed-up was 0.75 x 24 = 18 % of the CPU. The measured gain of the vector loops alone is below that, about 8 % of the wall.

What was kept, each measured on its own by switching it off in an otherwise final binary (`--water` alone on the multi-batch chunks, identical output each time, all-final reference 17.4 s wall, 138 s CPU):

| Change | Wall without it | CPU without it | Kind |
|---|---|---|---|
| `SampleBed`: the thread's last kilometre kept, cell read straight from the home grid | +2.7 s | +27 s | not SIMD |
| `Vector<int>`: column minimum in `RefineWet` (`MinOfRows`) and the distance transform's columns pass | +2.0 s | +20 s | SIMD (select, min, widen, int to float of exact squares) |
| `RefineWet`: no division per cell, no candidate list, in place (with the vector minimum above) | +2.5 s | +23 s | mostly not SIMD |
| Distance transform rows pass: all-site rows answered at once | +0.7 s | +11 s | not SIMD |
| `BoxMean`: tables over the rectangle of defined cells only (104 of 236 calls have none, the others 18,000 on average) | +1.0 s | +8 s | not SIMD |
| Distance transform rows pass: each parabola's value kept, not recomputed | +0.5 s | +6 s | not SIMD |
| Join steps: no array per pair | +0.3 s | +7 s | not SIMD |

Tried and dropped (no change beyond noise): skipping rows with no water in `RefineWet` (0.1 s), the same in the window fill (-0.1 s).
Left scalar on purpose: the rows pass of the distance transform and `SampleBed` (floating point with divisions and data dependent branches; a vector version would need gathers and would still be bound by the divisions), the running minimum along a row (serial), the `BoxMean` long to double conversion (no exact vector form on AVX2).
No FMA, no estimates, no reordering of any sum is used anywhere; the vector loops only select, compare, take minima and widen integers, and convert whole numbers below 2^24 to float.

Multi-batch region, whole pipeline, `sha256` of 4383 files compared with the `main` reference every time. Final binary (the kilometre cursor is owned by `ComputeTile`, see below), three runs, two of them interleaved with the previous `[ThreadStatic]` build to cancel machine drift:

| | Before (#1562) | After (final) | Previous build, interleaved |
|---|---|---|---|
| `water/C-beds` wall | 25.1, 24.9, 25.4 s | 16.9, 16.8, 16.9 s | 17.2, 17.4 s |
| `water/C-beds` CPU | 205, 205, 207 s | 133, 131, 132 s | 137, 138 s |
| Whole run | 104.3, 103.0, 105.1 s | 97.8, 95.9, 95.2 s | 97.4, 96.7 s |
| Peak working set of the run | 8497, 8549, 8578 MB | 9164, 8569, 9168 MB | 9286, 8970 MB |

`C-beds` is 33 % faster. Of that, the vectors are about 8 % and plain fixes about 25 %: switching the vector loops off cost 2.0 s of the 7.6 s gained, and with every vector path scalar (`DOTNET_EnableHWIntrinsic=0`: `C-beds` 19.9 s, whole run 101.5 s) the phase is still 21 % faster than before (an upper bound for the vectors: the switch also slows the BCL's own vector code).
Identical bytes (4383 of 4383) in all of these, with `DOTNET_EnableAVX2=0` (earlier build, `C-beds` 18.6 s) and `DOTNET_EnableHWIntrinsic=0`; benchmark 557 of 557, rural 1931 of 1931. Under a heap limit, final build: 16 GB `C-beds` 17.0 s, whole run 102.9 s, peak 8221 MB; 4 GB 21.6 s, 175.8 s, 4210 MB (before the change: 25.5 s / 110.0 s / 8086 MB and 35.8 s / 187.7 s / 4206 MB), identical. The 16, 8 and 4 GB runs of the intermediate `ThreadLocal` build (8 GB: `C-beds` 19.6 s, 118.7 s, 7974 MB, before 24.8 s / 120.6 s / 7792 MB) were identical too.

The sampling cursor: a first version kept the last kilometre in a `[ThreadStatic]` (it holds the source and a grid on every pool thread for the life of the process, bad for the in-process builders); a `ThreadLocal` instance field fixed that but cost 8 % of `C-beds` at 898 million calls. `BathySource.BedCursor` is a struct the caller passes by `ref` (`SampleBed(e, n, ref cursor)`; `SampleBed(e, n)` makes a fresh one), one per tile, used by the one thread that runs the tile; it was also 2-3 % faster than the thread static.

Peak working set: the run's peak is set by the network stage (8.47-9.16 GB from run to run, not touched here); the water stage itself now sometimes goes above it (8.6-9.2 GB in these runs, where before it stayed under the network peak).
On its own (`--water` over finished chunks) the stage peaks at 4.4-4.9 GB before and 5.5-6.7 GB after. It is not more live data (the pool allocates the same 2.2 GB) but large object heap garbage that waits longer for a gen 2 collection:
the stage allocates a quarter of what it did (81 GB before, 21 GB after) and so reaches fewer collections. A cure would be fewer or pooled large arrays per tile (`depth`, `kind`, the output grids), not measured. Under heap limits the collector works as before and the runs complete.

A native rewrite of these loops would meet the same limits: the vector loops that exist gave 2x where the loop only streams (columns pass 19 -> 9 thread-seconds), nothing where it is divisions and branches, and the window arrays are bound by memory bandwidth shared by the cores. The biggest remaining item is algorithmic, not a language one: a window is 2201 x 2201 round a 1000 x 1000 tile, so each lake vertex is sampled by up to nine windows (898 million `SampleBed` calls for 477 tiles). Sampling each kilometre once would cut that third of the phase to a ninth at the price of 8 MB per surveyed kilometre held. Not done: it raises the peak, which this change already did.

## Looked at, not changed

- **SQLite pragmas** (`mmap_size`, `cache_size`, `query_only`, `temp_store`): no gain warm on a GWR scan (2.60 -> 2.53 s) nor on the TLM read pattern. Cold cache not measured (no way to drop the cache here).
- **Water labels in the cache**: labelling is 0.9 s of 30 s CPU on the rural region; not worth the disk.
- **Terrain**: 73 % of its CPU is the zlib inflate of the source zips; the parser is hand-written already.
- **Places, POIs, open sources**: 3 s each, one core, whatever the region (full table scans, 3.4 M GWR rows take 2.6 s in SQLite alone). They only look big on small regions. Running
  open sources beside places and POIs would save 3 s but needs more than a line in `Preprocessor.cs`; POIs read the places' output, so those two cannot overlap.
- Repeated PBF decodes: separate processes in `rebuild-map.sh`, already parallel; left.

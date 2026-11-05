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
- Identical minimum and distance, faster walks only: `RefineWet` (van Herk), `DistanceTransform` (a row at a time).

## Measured (6-core 5600X, warm, Nov 2026)

Benchmark 52 tiles and rural region (`builder-bench`). Before is the binary after the RoadGen work. The rural region is 216 mountain tiles: forest, rock, scree, glacier and the big polygons they come in, 2.4 % water, no survey.

| Run | Stage | Wall | CPU | Cores | GC pause | Peak MB | Identical |
|---|---|---|---|---|---|---|---|
| Benchmark, before | whole run | 155.6 s | 278 s | | 4.7 s | 5033 | reference |
| ... after | | 97.6 s | 229 s | | 4.3 s | 7650 | 557 of 557 |
| ... before | cover | 55.7 s | 57.2 s | 1.03 | 0.15 s | | |
| ... after | | 1.9 s | 7.2 s | 3.77 | 0.12 s | | |
| ... before | water | 6.7 s | 25.7 s | 3.87 | 1.21 s | | |
| ... after | | 4.0 s | 25.5 s | 6.35 | 1.01 s | | |
| Rural, before | whole run | 91.7 s | 307 s | | 3.6 s | 7112 | reference |
| ... after | | 58.2 s | 285 s | | 3.8 s | 8995 | 1931 of 1931 |
| ... before | cover | 35.7 s | 37.4 s | 1.05 | 0.17 s | | |
| ... after | | 2.5 s | 13.1 s | 5.30 | 0.16 s | | |
| ... before | water | 5.8 s | 42.2 s | 7.26 | 0.87 s | | |
| ... after | | 4.2 s | 29.7 s | 7.03 | 0.81 s | | |

Same bytes at `--jobs 1` and `--jobs 3`, with `DOTNET_TieredPGO=0` and `DOTNET_TieredCompilation=0`, with a 16 GB and a 6 GB heap limit, and with rings painted every 1,024 coordinates
(the bound on queued rings forced to flush mid-layer). What is left of a run is the network stage (69 of 98 s, 21 of 58 s), then terrain and the constant three.

The gains, each proved alone on both regions: per-tile painting (benchmark cover/extract 51 -> 0.65 s, rural 27.8 -> 1.9 s), all cores for the rest of cover, then water.

## Looked at, not changed

- **SQLite pragmas** (`mmap_size`, `cache_size`, `query_only`, `temp_store`): no gain warm on a GWR scan (2.60 -> 2.53 s) nor on the TLM read pattern. Cold cache not measured (no way to drop the cache here).
- **Water labels in the cache**: labelling is 0.9 s of 30 s CPU on the rural region; not worth the disk.
- **Terrain**: 73 % of its CPU is the zlib inflate of the source zips; the parser is hand-written already.
- **Places, POIs, open sources**: 3 s each, one core, whatever the region (full table scans, 3.4 M GWR rows take 2.6 s in SQLite alone). They only look big on small regions. Running
  open sources beside places and POIs would save 3 s but needs more than a line in `Preprocessor.cs`; POIs read the places' output, so those two cannot overlap.
- Repeated PBF decodes: separate processes in `rebuild-map.sh`, already parallel; left.

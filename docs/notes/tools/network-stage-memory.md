# Road network stage: memory is bounded by a block, not by the run (#1425)

The nationwide features pass died with "Out of memory" in `TileRewriter.Run` after block 410 of 1421
(16 GB heap): the `Session` kept state from every block done.

- `Footprints` (building roof triangles per tile + answered points) is made per block in `Session.Block`, never per session.
- `NetworkStats.Layouts` (every `JunctionPlan`, read only by the summary lines) is filled only when `Options.KeepPlans`;
  `RoadStage.RunNetwork` keeps them up to 256 tiles, `RoadGen --drop-plans` turns them off.
- Anything new on `Session` or `NetworkStats` must stay small (counters, short lists), never per-junction objects.

Measured on 576 tiles around Zurich (24 x 24, 16 blocks, 2.2 M OSM rows): peak RSS 8.3 GB -> 5.3 GB, and 7.5 GB -> 3.7 GB
at the end instead of growing; `.road` output byte-identical (576 files), time 880 s -> 861 s.

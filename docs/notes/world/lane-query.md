# Lane graph query without a world (`--lanequery`, #1760, #1758)

- **What**: `src/World/LaneQuery.cs`, a no-world probe (`ClientWorld` table) that reads traffic's car lane graph straight
  off the LV95 road tiles: a few hundred MB and seconds, where the long drive (`earth-bar-probes`) needs the whole
  streamed world (it was killed by the RAM guard on a shared machine) to find the same cuts.
- **Command**: `godot --headless --path . -- --lanequery[,out.txt] --chunks DIR [--at E,N --to E,N] [--around E,N[,R]]
  [--segments] [--dump file.csv]`.
  - `--at/--to`: the long drive's route (`RaceRoute.ToAsync`, the same corridor and graph). Its log says where a way
    that keeps every one-way ends, how far the undirected graph reaches, and, when it must, the wrong-way route with
    **each one-way edge it took against its direction** (class, length, LV95). A short against-edge on that list is
    not always the cut: at 8× cost the route also takes one as a shortcut. The cut is where the directed way stops.
  - `--around`: within R m (default 150), every car edge (road / connector / junction path, class, flags, one-way,
    ends, lane offsets), each traffic island and signal pole, and the lane lines (`Traffic.KeepRight`) that run into
    an island. With a route, the route's points there, and where it passes within 1.5 m of a pole or inside an island.
  - `--segments`: the tiles' own segment records there (one-way, `RoadAttrFlags` such as `Osm`): whether a direction
    came from the OSM overlay or from RoadGen.
  - `--dump`: the same as CSV (`kind,id,class,flags,oneway,e,n`; a pole's flags may hold a comma, so read the last
    three columns from the end). A matplotlib plot of it showed both #1760's and #1758's causes at a glance.
- **Gotcha**: `RaceRoute.ToAsync` awaits the chunk source; called from the main thread with `.GetResult()` it
  deadlocks (its continuations wait for that thread). The probe runs it in `Task.Run`.
- **Rebuilt-tile check without touching D:**: copy the tiles' `chunk_*`/`roads_*` and `<temp>/roads_raw` for the
  area (plus `places.json`, `manifest.json`, `gantry_facts.tsv`, `osm_nodes.tsv`) to a scratch dir, run
  `TerrainPreprocessor --osm-overlay ... --tiles-file --temp` and `RoadGen --rewrite --chunks --temp --tiles-file`
  there, then `--lanequery --chunks <scratch>`. Build main's tools from `git archive origin/main tools/...` (with
  `tools/MapCore`) for the "before" run, so before and after differ only by the change.

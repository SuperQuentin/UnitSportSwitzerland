# Water pass: memory is bounded by three columns, not by the country (#1439)

The nationwide `--water` pass ran out of memory (16 GB and 22 GB heaps) right after its last tile, in `StitchDryNeighbours`,
which cached the whole 2 MB height grid of every rewritten tile (17,188 tiles = 34 GB).

- The stitch reads a rewritten tile only for its four edges (8 KB) and a dry neighbour only while it edits it.
- `BathySource` kept every survey kilometre it had decoded (up to 10 GB for the 2,659 km); `ReleaseBefore` drops those west of the
  columns the pass is on, like the tile cache (`held`) does.
- Anything new kept across tiles in `WaterStage.Run` must be a summary (counters, edges), never a grid.

Measured on the NAS (6 jobs, Léman and Lake Zurich regions): peak RSS 5.6 GB -> 3.5 GB on 902 water tiles, flat (3.2 GB) on 182;
managed heap after phase C 676 MB -> 35 MB (it grew with the tiles before); outputs byte-identical (8,408 and 1,049 files, and a
case where 25 dry neighbours really are stitched).

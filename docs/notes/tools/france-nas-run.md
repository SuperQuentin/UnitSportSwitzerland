# France terrain on the NAS (#1399)

All of mainland France and Corsica from IGN RGE ALTI 1 m (one folder per département under
`data/france/rgealti`), terrain only (no roads, buildings, water or cover: BD TOPO is a later issue),
built **on the TrueNAS box** into `terrain_chunks_france` with its own manifest, in tile format v3
(`terrain/height-format-v3`). It does not need swissALTI3D, does not touch the Swiss build and is not deployed.

```
tools/build-france.sh start     # publish (self-contained linux-x64: nothing installed on the NAS), copy, start detached; again = resume
tools/build-france.sh status    # process, RSS, last progress line and ETA, problems, tiles on disk
tools/build-france.sh stop      # SIGTERM (SIGKILL after 60 s): safe, files are written atomically
tools/build-france.sh test mont-blanc | camargue | E0,N0,E1,N1 Dxxx   # small foreground run: heights, time, peak RSS
tools/build-france.sh plan      # scan and tile choice only: counts and time, builds nothing
```

Env: `HEAP_GB` (GC hard limit, 16: the rest of the 31 GB stays for the ZFS ARC), `JOBS` (nproc),
`DEM_CACHE_MB` (6144), `NICE` (10), `MIN_PROBES` (1). Log: `terrain_chunks_france.log` next to the data.
It runs as `truenas_admin`, no root; the login shell there is zsh, so the script sends `bash -s`.

- **Reader** (`ForeignDem`): the whole tree, recursively, only `RGEALTI_FXX_<E>_<N>_MNT_*.tif`. It skips the
  SRC/DST companions (8-bit source and distance maps, never heights), metadata, supplements and `*.tmp`
  folders (extraction still running). A tile is placed **from its file name** and opened only when a block
  is read, then closed: ~500k open handles would break `ulimit -n` 1024 and ~500k parsed headers would cost
  minutes and GBs. Sample (0,0) is at the integer Lambert-93 metre of the name (IGN's convention; the header
  is checked against the name on the first read, a mismatch is ignored with a `[WARN]`). A tile delivered by
  two départements is read from either (the first with data). `--dem-only D074,D013` limits a test run.
- **Tile choice**: `--dem-box auto` (a 5 km lattice of LV95 points over the rasters, +10 km) and `TilesIn` by
  coverage: 10x10 probe points inside each LV95 tile, a probe counts when the French tile under it has data
  there. Only the ~3 % of tiles on the edge of the delivered area (a neighbour tile missing: coast, border)
  are read, once, to learn where their -99999 starts (a 10x10 mask of 100 m squares); the rest have all eight
  neighbours and are data throughout. `--dem-min-probes 1` keeps any tile with data; 13 (the Swiss default) is
  "half a tile". A tile chosen yet empty gets sea level, never 4700 m walls.
- **Projection**: `DemProjection` is rigorous (Bessel oblique Mercator inverse, ETRS89, Lambert-93; mm against GDAL)
  at any distance. Nothing on this path uses `SwissProjection`'s polynomials (valid ~100 km around
  Switzerland; they were 20-60 m off in Dunkirk and Camargue, which is enough to locate a box, not a seam).
  The game's GPX/search/place-name conversions (`SwissProjection`) are therefore off in France: a later concern.
  A 1 km LV95 tile is a little under 1 km on the ground far from Switzerland (oblique Mercator scale, ~0.3 % in the north).
- **Scale**: ~0.5 GB of heap for the index and the rasters, plus the decoded-tile cache (`--dem-cache-mb`,
  4 MB a tile), plus the band of tiles waiting for their neighbours' seams (a row or two of an LV95 row across
  France, ~3 GB worst case). A resume reads only the 32-byte header of every `.edge`
  (`EdgeStrips.IsCurrent`) and the edges themselves only for tiles next to unfinished ones. `manifest.json` is
  monolithic (tens of MB), written once at the end together with `horizon.bin`; a killed run has none until
  the next one finishes. Startup (scan, tile choice, edge masks) takes several minutes on each start.
- **Resumable**: a tile is done when its `.terr` is valid (v3 header, length) and its `.edge` in
  `terrain_chunks_france_temp` carries the same source stamp. A killed run restarts with the same command and
  redoes only the unfinished tiles and the seams next to them. The log has `[done/total] tiles/s, eta` every 5 s.
- **Vertical**: IGN69 heights as they are (dz 0). Corsica (D02A/D02B) is IGN78C, another vertical datum
  (a few decimetres from IGN69): accepted, not corrected.
- **Borders**: nodata beyond the French border (and the sea in a coastal tile) becomes copies of the nearest
  filled row/column (`XyzParser.FillMissing`): a plateau at the coast's height, not sea level, until a later
  water pass. Merging with swissALTI3D (dz -0.28 m) is `foreign-terrain`'s `--in` path, not this build.
- **Mont Blanc**: RGE ALTI stops at the border before the summit: its highest cell is 4,778.9 m (not
  4,806), proven above v2's 4,700 cap by the v3 tile (`test mont-blanc`: manifest max 4,778.85).

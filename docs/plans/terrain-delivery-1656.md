# #1656 Terrain delivery for the earth world

The user's design (Nov 10 2026, on the issue): ship a small base, stream every level, build each level from the finest
data, no hard-coded data path, no pops between levels. Everything here is behind `--frame earth`: the flat world is
deleted at D3. Body-agnostic where it costs nothing: H7 (other planets) reuses the same loading path with its own
pyramid.

## What exists

- `Globe` reads one folder (`GlobeFiles`: a disk folder or `res://assets/globe`) into two dictionaries of
  (file, offset, length), heights and colour, and decodes on its workers. `Globe.DataDir` falls back to
  `Globe.DefaultDir` = `D:/UnitSportSwitzerland/cube_global`, then the shipped levels 0-2 (23.5 MB: 513² heights, 12
  months of 256² WebP).
- The fill (`PyramidBase.Open`) reads only a disk pyramid with levels to 9 or finer; with the shipped one it keeps the
  generator's relief. Server and client must stand the fill on the same base (`--fillagree`; the deployed server runs
  with the shipped globe for that reason).
- Leaf streaming (#1655): `RequestCubeAsset(kind, face, level, x, y, section)`, protocol 95; a server answers
  "missing" to any kind it does not serve by key. An index on join (`AssetKind.LeafIndex`), a source chain
  (`LeafStore.UseRemote`), the tile cache, retries, Low data.

## The steps

1. **One source chain for the pyramid** (`Terrain/Leaves/PyramidSource`, Godot-free): an ordered list of sources, each a
   manifest and an index of entries, and the finest copy of each tile wins (most lattice bits; ties go to the first
   source). Sources: a local folder, the shipped base (through `GlobeFiles`), the server's (an index and a fetch).
   `Globe` and the fill read through it; `GlobeFiles` stays the file reader.
2. **No hard-coded path** (`TerrainPaths.FindGlobeDir`): `--globe DIR` (`off` none), else the chunk folder's
   `_globe` sibling (as `_cube` is the leaves'), else `cube_global` beside the chunk folder (the layout the tools write
   today, so the PC with D: keeps its pyramid without naming D:), else nothing local. `Globe.DefaultDir` goes.
3. **The server streams the pyramid**: `AssetKind.PyramidIndex` (22), the server's chain without the shipped base, every
   tile with its size and stamp; `AssetKind.Pyramid` (23) by key through `RequestCubeAsset`, section 0 the heights,
   section 1-12 one month's colour (a one-image `CUBC` entry, 1/12 of the whole). Cached in the tile cache
   (`pyr_<stem>_<stamp>.s<section>.cubt`). **No protocol bump**: an older server answers "missing" to both kinds,
   which leaves a new client on its local data and the shipped base; an older client never asks.
4. **A small shipped base** (`--cube-subset --cube-bits 6 --cube-albedo 64`): levels 0-2 of the globe and a coarse
   Switzerland (the levels over the leaves, down to ~2.5 km cells), 65² heights resampled exactly at the lattice
   points (a coarse vertex is a fine one: the streamed 513² tile meets it to the quantum), small colour. The globe
   draws 64 cells a tile whatever the bits (stride `(1 << bits) / 64`).
5. **Every level from the finest data**: `--cube-swiss` merges levels below its own (`--cube-levels 0-13`): a coarse
   tile over the leaves is the tent mean of the next finer level, and only those tiles and their siblings change; the
   rest of the level stays ETOPO's.
6. **The fill on the same chain**: on a client the fill waits for the server's pyramid index (or its "missing"), then
   stands on the chain; a server and a client stand it on the same base. The fill refuses a chain finer than the shipped
   base only where it has nothing at level 9 or finer (`PyramidBase.MinFinestLevel`, unchanged rule).
7. **Geomorphing** instead of skirts and every-8th-vertex pops: each vertex carries where the parent level draws it
   (CUSTOM0: the parent's position as an offset, the level's cell size), and the vertex shader morphs by its distance
   to the camera over the last part of the tile's range (`world_globe_morph`: focal / split pixels, a global). A tile
   at its far edge is its parent's shape, so the split and the merge change nothing on screen.
8. **Far features** (#1629's follow-up): scoped and filed as its own issue, not built here.

## Decided without the user (review)

- **No protocol bump.** New asset kinds on the existing `RequestCubeAsset`, as #1380's digest did: an older peer
  answers or asks nothing new. D2's protocol 96 is left alone.
- **Colour by month as a section**: a client fetches the month it draws (1/12 of a tile's colour), not all twelve.
- **The finest copy wins** across sources, ties to the local data: the shipped 65² base never hides a streamed 513²
  tile, and a local full pyramid never waits on the network.
- **Local install** is found from the data location (`--chunks`, `UNITSPORT_CHUNKS`, `terrain_location.json`), not a
  drive letter.
- **Skirts stay as a short crack guard** (a tenth of a cell): the forced splits round drawn leaves (#1800) can still
  put two levels side by side outside the morph's range, and a crack there shows the sky.
- **The server serves its own pyramid only** (disk), never the shipped base (every client has that).
</content>
</invoke>

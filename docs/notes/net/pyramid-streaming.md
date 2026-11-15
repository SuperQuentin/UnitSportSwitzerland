# Pyramid streaming: the globe's levels from the server, a small shipped base (#1656)

Under `--frame earth` the globe's pyramid (the coarse cube levels the far view and the fill stand on) comes from three
places in one chain, `Terrain/Leaves/PyramidSource` (Godot-free; the game's copy is `World/GlobeData.Earth`):

1. **The local install**: `TerrainPaths.FindGlobeDir`: `--globe DIR`, else the chunk folder's `_globe` sibling, else
   `cube_global` beside the chunk folder (the PC with D: finds `D:/UnitSportSwitzerland/cube_global` through its
   `terrain_location.json`; nothing names a drive). `--globe res://assets/bodies/earth` means the shipped base alone,
   `--globe off` no globe.
2. **The server's**, once its index arrives (`UseRemote`).
3. **The shipped base**, `res://assets/bodies/earth`.

Per tile the **finest copy wins** (most lattice bits), ties to the earlier layer. A layer is a manifest and its tiles
(`PyramidSource.Layer`/`Tile`: bits, heights reader, colour reader); `GlobeFiles.Layer` reads a folder or the pack.
Body-agnostic: another body's pyramid is another `PyramidSource` (H7).

## The wire (no protocol bump)

- `AssetKind.PyramidIndex` (22) on join, beside the leaf index: `PyramidIndex` (`PYDX`): every tile on the server's
  disk (never the shipped base) with its bits, heights size, colour flag and stamp (FNV-1a of the key and the heights
  entry), and the lakes. 23 B a tile, ~94 KB for the D: pyramid. Built once on a worker when `ChunkStreamer.Pyramid`
  is set (`ServerWorld`, earth servers with a local pyramid), the answer's worker waits for it.
- `AssetKind.Pyramid` (23) by key through `RequestCubeAsset` (protocol 95's RPC): section 0 the heights (`CUBT` as
  stored), 1-12 one month's colour (a one-image `CUBC`, `PyramidSource.ColourEntry`). Sent as stored (both compressed).
- **A body** (H7's planets): `AssetStream.CubeSection` packs the body in the high bits of `RequestCubeAsset`'s
  section (the Earth 0: the same bytes as before), and the index asks name it by their tile's E. A server answers
  "missing" for a body it does not serve. `CubeKey` stays body-free. See "Other bodies" below.
- An older server answers "missing" to both: the client keeps its local data and the shipped base. An older client
  never asks. So D2's protocol 97 is untouched.
- Client: `NetworkChunkSource.FetchPyramid`: cache `pyr_<stem>_<stamp>.s<section>.cubt` in the tile cache (cap, Clear,
  trim), the leaves' retries and "the server has none" memory, through **three slots of its own** (`_pyramidSlots`, none
  of the near ground's six or the two section slots). Through the section slots (shared with the leaves' far heights) a
  climb streamed ~3 tiles a second and drew levels 4 coarser than a copy with the pyramid on disk (Nov 14). Low data: levels past `LowDataPyramidLevel` (6) from the cache only.
- `ClientTerrainSync.WantsPyramid` / `PyramidIndexSettled` (fired either way); `ClientWorld` hands the index to the
  chain, `Globe.RefreshPyramid(changed)` (a changed tile is read again in place) and the fill (below). A world left
  drops the server's layer (`DropRemote`).

## The fill stands on the server's pyramid

The fill must be built on the same base on the server and every client (`--fillagree`, the 20 m of #1793):
- a server and an offline copy: `PyramidSource.Base()`, this copy's own tiles at the full lattice (never the shipped
  base's resampled ones, never streamed ones);
- a client online: `EarthLeaves.AwaitPyramid()` holds every fill leaf back until the index settles, then
  `UsePyramid(chain.ServerBase())`: the server's tiles alone, each read from this copy's disk only where its stamp is the
  server's, else streamed; or the generator's relief when the server has none (as the server). `EarthWorld.Reread`
  reads every loaded leaf again.
- ponytail: a tile that does not come stays unread while `PyramidBase` keeps it; the leaves made meanwhile stand on a
  coarser level there.

## The shipped base (`--globebase`, `World/Globe/GlobeBaseBake`)

```
<godot> --headless --path . -- --globebase OUT --globe <full pyramid> --country 45.8,5.9,47.85,10.55,8
```
Levels 0-2 and Switzerland's box to level 8 (with siblings and ancestors) at 65² (`--bits 6`: every 8th lattice point
of the 513² tile, exactly), height quantum a thousandth of the base's cell (8 m at most), colour 64² for four seasons
(`--months 1,4,7,10`, `CubeAlbedo.ForMonth` takes any divisor of 12) on levels 0-4: 318 tiles, 1.0 MB heights + 0.5 MB
colour (was 23.5 MB for levels 0-2). The globe draws any tile at `Globe.Cells` (64) cells (`StrideOf(bits)`), so a 65²
tile and a 513² one draw the same vertices. Needs a pyramid built with `--cube-swiss ... --cube-levels 0-13
--cube-country 45.8,5.9,47.85,10.55,8` (`tools/cube-tiles`).

## Checks

- `PyramidStreamTests`: the index, the chain (finest wins, ties local, the same index changes nothing, `DropRemote`),
  the server's base (local where the stamp agrees).
- `tools/pyramidstreamcheck.sh`: a server with leaves and the pyramid, client A with both, client B with neither; both
  climb 2 km to 2,000 km in 60 s and hover 10 s (`--globeclimb`): B streams and caches tiles, draws A's level 0.8
  altitudes ahead at the top, and pops no more. The levels on the way show the stream's lag.
- `tools/fillagreenetcheck.sh` run `pyramid`: the client with no pyramid builds the server's fill to the centimetre.
- `--globeclimb FROM,TO,SECONDS` (`World/GlobeClimb`): pops on a climb, `--nomorph` for the before (`globe-geomorph`).

## Other bodies (#1849)

Every body but the Earth and the Sun has the same chain, `World/Bodies/BodyData.For(body)`, finest copy first:

1. **The local install**: `<dir>/<body>/cube` under `TerrainPaths.FindBodiesDir`: `--bodies DIR` (an override now, never
   needed), else the "bodies" of a `terrain_location.json`, else `planets` or `bodies` beside the chunk folder. This PC's
   data is `E:/UnitSportSwitzerland/planets` (add `"bodies": "E:\UnitSportSwitzerland\planets"` to the main checkout's
   `terrain_location.json`, or pass `--bodies`).
2. **The server's**: on join the client asks each body's index (`PyramidIndex` with the body in the tile's E, cached as
   `body<N>_pyramid_index`), then its tiles by key with the body in the section's high bits, cached as
   `pyr<N>_<key>_<stamp>.s<section>.cubt`. `ChunkStreamer.ServeBody` (set by `ServerWorld` for each body on the server's
   disk) builds the index on a worker. `BodyGlobe.RefreshPyramid` deepens the globe and reads changed tiles again (the
   old drawn until the new one is in); `BodyGround.Refresh` makes the heights again at the new finest level and
   rebuilds each square where it stands.
3. **The shipped base**: `res://assets/bodies/<body>` (Git LFS), levels 0-2 (126 tiles; 30 for Deimos and the giants,
   whose pyramids stop at 1), 65² heights, one colour image: 256² for the Moon and Mars, 128² for the rest (four tiles a
   face at level 2: the 2k sky maps' resolution). 7.1 MB for the fifteen bodies (Mercury 1.0, Venus 1.2, Moon 1.8, Mars
   1.0, the moons 0.1-0.4, the giants 40-80 KB) beside the Earth's 1.6. Rebuilt with
   `--globebase assets/bodies/<body> --globe <dir>/<body>/cube --bits 6 --albedo 256|128 --months 1 --levels 0-2 --albedo-levels 0-2`.
4. **Generated** (`BodyData.Generated`), for a body none of these has: levels 0-1 on the reference surface coloured from
   its 2k sky map (`GlobeAlbedoBake.Pixels`), or its sky tint where it has no map; the profile's craters and relief come
   under it as everywhere. No body needs it today (each has a base); no albedo-to-height guess yet.

**On the shipped base alone** the near ground reads level 2 (the Moon's cells ~10 km, Mars' ~13 km), bilinear, with the
profile's craters, dunes or ridges under it: the right altitude (Gale's floor −4,852 m against −4,900 m from MOLA's level
4) and the right look up close, without the data's own 0.1-1 km relief (the Moon's crater rims, Olympus' scarps). A tile
the chain lacks at the finest level is its deepest ancestor's, resampled (`BodyGround.FromAncestor`), never 0 m.
Checks: `--mooncheck [body] --fromorbit` with no body data (`--mooncheck mars --fromorbit` lands at Gale crater, `--at
LAT,LON` elsewhere); `tools/bodystreamcheck.sh` (a server with the data, a client with none: the Moon's index, its ground
read at level 5, and `--bodyfetch moon,LAT,LON` streaming the finest tile under Apollo 11 into the cache; standing on another
body online waits for C3b, #1975).

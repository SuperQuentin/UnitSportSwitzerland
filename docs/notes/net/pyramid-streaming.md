# Pyramid streaming: the globe's levels from the server, a small shipped base (#1656)

Under `--frame earth` the globe's pyramid (the coarse cube levels the far view and the fill stand on) comes from three
places in one chain, `Terrain/Leaves/PyramidSource` (Godot-free; the game's copy is `World/GlobeData.Earth`):

1. **The local install**: `TerrainPaths.FindGlobeDir`: `--globe DIR`, else the chunk folder's `_globe` sibling, else
   `cube_global` beside the chunk folder (the PC with D: finds `D:/UnitSportSwitzerland/cube_global` through its
   `terrain_location.json`; nothing names a drive). `--globe res://assets/globe` means the shipped base alone,
   `--globe off` no globe.
2. **The server's**, once its index arrives (`UseRemote`).
3. **The shipped base**, `res://assets/globe`.

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
- **Room for a body** (H7's planets): `AssetStream.CubeSection` packs the body in the high bits of `RequestCubeAsset`'s
  section (the Earth 0: the same bytes as before), and the index asks name it by their tile's E. A server answers
  "missing" for a body it does not serve (all but the Earth today). `CubeKey` stays body-free; a body's local install
  is meant to live at `<root>/<body>/cube_global` (and `<body>/leaves`), the Earth's keeping today's paths.
- An older server answers "missing" to both: the client keeps its local data and the shipped base. An older client
  never asks. So D2's protocol 96 is untouched.
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

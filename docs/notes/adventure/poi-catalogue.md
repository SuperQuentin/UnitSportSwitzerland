# POI catalogue at runtime (#1224)

`src/Adventure/Places/PoiCatalogue.cs`: the region's points of interest from `pois.bin`
(`adventure/poi-pipeline`), the same on the server and every client. Plain C#, no Godot, linked into
tier 0.

## API

- `Resolve(idOrAlias)`: a canonical id matches on tag + hash (any slug: a TLM rename keeps progress);
  else a curated alias added with `AddAliases` (returns the ones that resolve to nothing). Saved progress
  always holds the canonical string.
- `Near(e, n, radius, into, kind?)`: clears `into`, then adds the POIs within the radius. Buckets per 1 km
  `TileId`; lakes (which span tiles) are kept apart and found by their bounding box. **Allocates nothing**
  into a reused list (a test measures it): call it from a 1 Hz tick with one list per caller.
- `InTile(TileId)` (a `ReadOnlySpan`), `InCanton(code)`, `OfKind(kind)`, `Totals(code)` (tier 1/2 and land
  fog cells, from the file), static `InLake(lake, e, n, buffer)`.
- `Empty` (generated world, an older server, a region without `--pois`), `Fixture(startE, startN)` /
  `FixtureData` (ten POIs round the fixture start, canton VS: a crossed peak 72 m away, a pass, a tower,
  a chapel, a station, a village bus stop, a cable-car top, a castle, the lake course's landing and its
  ellipse as the lake).

## Loading: `PoiCatalogueHost`

- `Current` is never null, replaced whole on the main thread, then `Changed`. A stand-in until the places
  service (plan 3, after #1223) owns the catalogue.
- Server: `ServerWorld` loads it from its chunk source at boot. Client: `ClientWorld` loads it from the
  local source at boot, and again on `ClientTerrainSync.PoisReceived` (the server's file in the cache dir,
  read through `NetworkChunkSource.LoadPoisAsync`: shipped first, else the cache).
- `--pois fixture`: `Fixture()` around the fixture start instead of any file.
- `WorldStatics.Reset` empties it on leaving to the title.

## Sync

- `AssetKind.Pois = 17`, tile 0,0, `SyncFileAsync` (`net/region-file-sync`), served over ENet and the HTTP
  mirror (`AssetStream.ServedOverHttp`), deflated on the wire. 398 KB for the 6 699-tile dev region.
- An older server answers "missing" (`Enum.IsDefined`): one log line, an empty catalogue. An older client
  never asks. So no protocol bump yet: the plan's single bump comes with its RPCs (discovery, fog).

## How to check

- `tools/test.sh unit`: `PoiCatalogueTests` (seams, no allocation, tiles, cantons, lakes, fixture round trip).
- Quick tier: `--poischeck --world fixture --pois fixture` (`PoiCheck`): the boot load, the queries round the
  start, then a `pois.bin` written to `test_output/poischeck` read back through a `LocalChunkSource`.
- Net tier: `@netsmoke` covers the sync path with a server that has no `pois.bin` ("missing").

# Ski slopes from OSM (#887)

- **Source**: OpenStreetMap `piste:type` ways and relations (swissTLM3D has none:
  `swisstlm3d-maps-no-ski-pistes`). Kept: downhill, connection, snow_park, nordic, sled. Left out:
  skitour and hike, which are routes over unprepared snow. A `type=route` relation lends its tags to
  member ways with no `piste:type` of their own, under the relation's id (top bit set).
- **Counted on the CH extract (Oct 2026)**: 9,418 downhill ways (6,187 km), 4,854 nordic, 597 sled.
  Almost all have a difficulty. Only 727 are areas (closed with `area=yes`), and only 36 have a width.
  So line slopes take `PisteStage.DefaultWidth`:

  | Slope | Width |
  |---|---|
  | novice, easy | 40 m |
  | intermediate | 30 m |
  | advanced | 25 m |
  | expert, freeride | 20 m |
  | snow park | 40 m |
  | connection | 8 m |
  | nordic | 6 m |
  | sled | 4 m |
- **Format**: `pistes_E_N.pst` (`PisteFormat`, "USPS" v1, header flag `Osm`). It holds:
  - a record table: OSM id, type, difficulty, grooming, area or line, width, name, ref, and the path (tile-local, kept whole);
  - two rasters on the 1 m terrain grid (row 0 = north, like `.cover`), deflated: a slot byte
    (record + 1) and an aux u16 (edge mask over the outer 3 m in the high byte, groom direction
    modulo π in the low byte; for areas that is the outline's long axis).
  - Overlaps: the deeper-inside slope wins, then a line over an area.
  - At most 255 slopes a tile; the report warns when that limit drops one.
- **Commands**:
  - build: `TerrainPreprocessor --out <chunks> --pistes --osm-pbf <switzerland.osm.pbf> [--tiles-file f]`
    writes the tiles and `pistes_report.txt`, and deletes its own stale files;
  - self-check: `--pistes-check`;
  - MapSetup: `--layers pistes` (never part of `all`, ODbL: `osm-odbl-licence`).
- **Measured on western Valais** (6,699 tiles): 3,921 slopes, 976 tiles with a file, 4,443 ha,
  30.5 MB, 13 s.
- **Game**: `IChunkSource.LoadPistesAsync` in every source; streamed as `AssetKind.Pistes` 15.
  A missing file means no slopes there.
